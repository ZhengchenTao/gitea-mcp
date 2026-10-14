#!/usr/bin/env python3
"""Opt-in live MCP/Gitea integration test. Remote writes require a typed confirmation."""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen


ISSUER = "https://gitea-mcp-integration.local"
ALLOWED_TEST_REPOS = {
    ("zhengchen.tao", "gitea-mcp-write-test"),
    ("zhengchen.tao", "gitea-pr-selftest"),
}
PROTOCOL_VERSION = "2025-03-26"


class IntegrationError(RuntimeError):
    pass


class ToolFailure(IntegrationError):
    pass


def required_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise IntegrationError(f"Required environment variable is missing: {name}")
    return value


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def make_token(key: str, scope: str) -> str:
    now = int(time.time())
    header = b64url(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    payload = b64url(json.dumps({
        "iss": ISSUER,
        "aud": "gitea",
        "sub": "local-gitea-mcp-integration",
        "scope": scope,
        "iat": now,
        "exp": now + 3600,
    }, separators=(",", ":")).encode())
    unsigned = f"{header}.{payload}"
    signature = hmac.new(key.encode(), unsigned.encode(), hashlib.sha256).digest()
    return f"{unsigned}.{b64url(signature)}"


def parse_mcp_response(raw: bytes) -> dict:
    text = raw.decode("utf-8", errors="replace")
    for line in text.splitlines():
        if line.startswith("data: "):
            return json.loads(line[6:])
    if not text.strip():
        return {}
    return json.loads(text)


class McpSession:
    def __init__(self, endpoint: str, token: str):
        self.endpoint = endpoint
        self.token = token
        self.session_id: str | None = None
        self.next_id = 1

    def request(self, method: str, params: dict | None = None, notification: bool = False) -> dict:
        message: dict = {"jsonrpc": "2.0", "method": method}
        if not notification:
            message["id"] = self.next_id
            self.next_id += 1
        if params is not None:
            message["params"] = params

        headers = {
            "Authorization": f"Bearer {self.token}",
            "MCP-Protocol-Version": PROTOCOL_VERSION,
            "Accept": "application/json, text/event-stream",
            "Content-Type": "application/json",
        }
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id

        request = Request(self.endpoint, data=json.dumps(message).encode("utf-8"), headers=headers, method="POST")
        try:
            with urlopen(request, timeout=45) as response:
                if method == "initialize":
                    self.session_id = response.headers.get("Mcp-Session-Id")
                raw = response.read()
        except HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            raise IntegrationError(f"Local MCP HTTP request failed ({error.code}): {body[:500]}") from error
        except (URLError, TimeoutError) as error:
            raise IntegrationError(f"Local MCP endpoint is unavailable: {type(error).__name__}") from error
        return parse_mcp_response(raw)

    def initialize(self) -> None:
        result = self.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "gitea-write-integration", "version": "1.0"},
        })
        if result.get("error"):
            raise IntegrationError(f"MCP initialize failed: {result['error'].get('message', 'unknown error')}")
        if not self.session_id:
            raise IntegrationError("MCP initialize returned no session ID; cannot safely continue the HTTP integration run.")
        self.request("notifications/initialized", notification=True)

    def list_tools(self) -> dict[str, dict]:
        response = self.request("tools/list", {})
        return {tool.get("name", ""): tool for tool in response.get("result", {}).get("tools", [])}

    @staticmethod
    def tool_value(result: dict) -> dict | list | str:
        if "structuredContent" in result:
            return result["structuredContent"]
        text = "\n".join(block.get("text", "") for block in result.get("content", []))
        try:
            return json.loads(text)
        except (json.JSONDecodeError, TypeError):
            return text

    @staticmethod
    def error_text(result: dict) -> str:
        if result.get("error"):
            return str(result["error"].get("message", "MCP request failed"))
        tool_result = result.get("result", {})
        return "\n".join(block.get("text", "") for block in tool_result.get("content", []))

    def call(self, name: str, arguments: dict) -> dict | list | str:
        response = self.request("tools/call", {"name": name, "arguments": arguments})
        if response.get("error") or response.get("result", {}).get("isError"):
            raise ToolFailure(self.error_text(response) or f"Tool {name} returned an error")
        return self.tool_value(response.get("result", {}))

    def expect_error(self, name: str, arguments: dict, expected: str) -> None:
        try:
            self.call(name, arguments)
        except ToolFailure as error:
            if expected.casefold() not in str(error).casefold():
                raise IntegrationError(
                    f"{name} failed for an unexpected reason; expected {expected!r}, got {str(error)[:300]!r}") from error
            return
        raise IntegrationError(f"{name} unexpectedly succeeded; expected an error containing {expected!r}")

    def expect_error_any(self, name: str, arguments: dict, expected: tuple[str, ...]) -> None:
        try:
            self.call(name, arguments)
        except ToolFailure as error:
            if not any(value.casefold() in str(error).casefold() for value in expected):
                raise IntegrationError(
                    f"{name} failed for an unexpected reason; expected one of {expected!r}, got {str(error)[:300]!r}") from error
            return
        raise IntegrationError(f"{name} unexpectedly succeeded; expected an error containing one of {expected!r}")


def choose_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def wait_for_health(process: subprocess.Popen, base_url: str, timeout_seconds: float = 25) -> None:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if process.poll() is not None:
            raise IntegrationError("Local gitea-mcp process exited before becoming healthy; run dotnet build first.")
        try:
            with urlopen(base_url + "/healthz", timeout=1) as response:
                if response.status == 200:
                    return
        except Exception:
            time.sleep(0.25)
    raise IntegrationError("Local gitea-mcp did not become healthy within 25 seconds.")


def verify_test_repo_read_access(instance: str, owner: str, repo: str, token: str) -> None:
    request = Request(
        f"{instance.rstrip('/')}/api/v1/repos/{owner}/{repo}",
        headers={
            "Authorization": f"token {token}",
            "Accept": "application/json",
            "User-Agent": "gitea-mcp/1.0",
        },
        method="GET",
    )
    try:
        with urlopen(request, timeout=15) as response:
            repository = json.loads(response.read().decode("utf-8"))
    except HTTPError as error:
        status = error.code
        upstream = error.headers.get("Server", "unknown") if error.headers else "unknown"
        has_cf_ray = bool(error.headers and error.headers.get("CF-Ray"))
        cf_mitigated = bool(error.headers and error.headers.get("CF-Mitigated"))
        cause = "upstream rejection"
        api_message = "unavailable"
        try:
            raw_message = json.loads(error.read().decode("utf-8", errors="replace")).get("message", "")
            message = raw_message.casefold()
            api_message = " ".join(str(raw_message).split())
            api_message = re.sub(r"(?i)(token|authorization|pat)\s*[:=]\s*\S+", r"\1=[redacted]", api_message)
            api_message = re.sub(r"\b(?:gitea|glpat|gh[pousr])_[A-Za-z0-9_-]{16,}\b", "[redacted]", api_message)
            api_message = re.sub(r"\b[a-fA-F0-9]{40,}\b", "[redacted]", api_message)[:140]
            if "scope" in message:
                cause = "token scope"
            elif "permission" in message:
                cause = "account repository permission"
            elif "token" in message or "auth" in message:
                cause = "token authentication"
        except Exception:
            pass
        content_type = error.headers.get("Content-Type", "unknown") if error.headers else "unknown"
        error.close()
        raise IntegrationError(
            f"Dedicated read PAT cannot access {owner}/{repo} (HTTP {status}, cause={cause}, api_message={api_message}, content_type={content_type}, upstream={upstream}, cloudflare={has_cf_ray}, mitigated={cf_mitigated})."
        ) from None
    except Exception as error:
        raise IntegrationError(
            f"Dedicated read PAT repository preflight failed ({type(error).__name__})."
        ) from None
    if repository.get("full_name", "").casefold() != f"{owner}/{repo}".casefold():
        raise IntegrationError("Dedicated read PAT resolved to a different repository identity.")


def read_local_json(url: str) -> dict:
    try:
        with urlopen(url, timeout=5) as response:
            return json.loads(response.read().decode("utf-8"))
    except Exception as error:
        raise IntegrationError(f"Could not read local discovery metadata: {type(error).__name__}") from error


def require_object(value: object, label: str) -> dict:
    if not isinstance(value, dict):
        raise IntegrationError(f"{label} did not return a structured object.")
    return value


def main() -> int:
    instance = required_env("GITEA_TEST_INSTANCE").rstrip("/")
    owner = required_env("GITEA_TEST_OWNER")
    repo = required_env("GITEA_TEST_REPO")
    test_account = required_env("GITEA_TEST_ACCOUNT")
    read_pat = required_env("GITEA_TEST_READ_PAT")
    write_pat = required_env("GITEA_TEST_WRITE_PAT")
    jwt_key = required_env("GITEA_TEST_JWT_KEY")

    parsed = urlparse(instance)
    if (parsed.scheme != "https" or parsed.hostname != "git.zhengchentao.win" or
            parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/")):
        raise IntegrationError("GITEA_TEST_INSTANCE must be https://git.zhengchentao.win (the verified instance).")
    if (owner, repo) not in ALLOWED_TEST_REPOS:
        allowed = "zhengchen.tao/gitea-mcp-write-test or zhengchen.tao/gitea-pr-selftest"
        raise IntegrationError(f"Real writes are restricted to a dedicated test repository ({allowed}).")
    if test_account.casefold() != "gitea-mcp-test-bot":
        raise IntegrationError("Real writes require tokens issued by the secure wrapper for gitea-mcp-test-bot.")
    if read_pat == write_pat:
        raise IntegrationError("Read and write PATs must be separate credentials.")
    if len(jwt_key) < 32:
        raise IntegrationError("GITEA_TEST_JWT_KEY must be a local-only key with at least 32 characters.")
    verify_test_repo_read_access(instance, owner, repo, read_pat)
    project_root = Path(__file__).resolve().parents[1]
    app_dll = project_root / "bin" / "Debug" / "net10.0" / "gitea-mcp.dll"
    if not app_dll.exists():
        raise IntegrationError("Build the project first: dotnet build gitea-mcp.csproj")

    port = choose_port()
    local_base = f"http://127.0.0.1:{port}"
    local_endpoint = local_base + "/mcp"
    run_id = time.strftime("%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3)
    branch = f"codex-mcp-test-{run_id}"
    read_only_branch = f"feature/read-only-should-deny-{run_id}"
    unallowlisted_repo = f"not-allowlisted-{run_id}"
    file_root = f"integration-tests/{run_id}"
    created_path = f"{file_root}/created.txt"
    deleted_path = f"{file_root}/delete-me.txt"
    standalone_path = f"{file_root}/standalone-delete.txt"
    atomic_probe_path = f"{file_root}/must-not-partially-appear.txt"
    initial_text = f"Created by the Gitea MCP integration test {run_id}.\n"
    delete_text = f"This file is deleted in the second integration commit {run_id}.\n"
    updated_text = f"Updated by the Gitea MCP integration test {run_id}.\n"
    standalone_text = f"Created and deleted with the single-file tools {run_id}.\n"
    pr_title = f"Gitea MCP integration test {run_id}"
    pr_body = f"Automated integration test run `{run_id}`. This PR is in the dedicated test repository."
    read_token = make_token(jwt_key, "read:gitea")
    write_token = make_token(jwt_key, "read:gitea write:gitea")

    child_env = os.environ.copy()
    for key in ("GITEA_ADMIN_PAT", "GITEA_TEST_READ_PAT", "GITEA_TEST_WRITE_PAT", "GITEA_TEST_JWT_KEY", "GITEA_TEST_ACCOUNT"):
        child_env.pop(key, None)
    child_env.update({
        "Gitea__BaseUrl": instance,
        "Gitea__AdminPat": read_pat,
        "Gitea__WritePat": write_pat,
        "Gitea__WriteRepoAllowlist": f"{owner}/{repo}",
        "Jwt__Algorithm": "HS256",
        "Jwt__Issuer": ISSUER,
        "Jwt__Audience": "gitea",
        "Jwt__SigningKey__Current": jwt_key,
        "ASPNETCORE_URLS": local_base,
    })
    process = subprocess.Popen(
        ["dotnet", str(app_dll), "--urls", local_base],
        cwd=project_root,
        env=child_env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    write_authorized = False
    branch_created = False
    last_known_head: str | None = None
    pull_number: int | None = None
    phase = "preflight"
    try:
        wait_for_health(process, local_base)
        for metadata_path in ("/.well-known/oauth-authorization-server", "/.well-known/oauth-protected-resource"):
            metadata = read_local_json(local_base + metadata_path)
            if not {"read:gitea", "write:gitea"}.issubset(set(metadata.get("scopes_supported", []))):
                raise IntegrationError(f"{metadata_path} does not advertise both Gitea scopes.")

        session = McpSession(local_endpoint, write_token)
        session.initialize()
        tools = session.list_tools()
        if len(tools) != 24:
            raise IntegrationError(f"Expected 24 MCP tools, but tools/list returned {len(tools)}.")
        required_tools = {"read_repo", "list_branches", "list_tree", "read_file", "create_branch", "upsert_file", "delete_file", "commit_files", "get_diff", "create_pull_request", "read_pull"}
        missing = required_tools - tools.keys()
        if missing:
            raise IntegrationError(f"MCP server is missing expected tools: {', '.join(sorted(missing))}")
        for tool_name in ("upsert_file", "delete_file", "commit_files"):
            required = set(tools[tool_name].get("inputSchema", {}).get("required", []))
            if "expectedHeadSha" not in required:
                raise IntegrationError(f"{tool_name} does not require expectedHeadSha in its MCP schema.")
        changes_schema = tools["commit_files"].get("inputSchema", {}).get("properties", {}).get("changes", {})
        change_properties = changes_schema.get("items", {}).get("properties", {})
        if not {"operation", "path", "content", "expectedSha"}.issubset(change_properties):
            raise IntegrationError("commit_files.changes is missing a file-operation schema property.")

        repo_info = require_object(session.call("read_repo", {"owner": owner, "repo": repo}), "read_repo")
        if repo_info.get("private") is not True or repo_info.get("archived") is True:
            raise IntegrationError("The dedicated test repository must be private and not archived.")
        base_branch = repo_info.get("default_branch") or "main"
        branches = session.call("list_branches", {"owner": owner, "repo": repo})
        base_info = next((item for item in branches if item.get("name") == base_branch), None)
        protected_branch_info = next(
            (item for item in branches if item.get("name") != base_branch and item.get("protected") is True),
            None,
        )
        if base_info is None or not base_info.get("last_commit", {}).get("sha"):
            raise IntegrationError("Could not read the default branch HEAD from the test repository.")
        if base_info is None or base_info.get("protected") is not True:
            raise IntegrationError("Protect the test repository's default branch in Gitea before running this script.")
        if protected_branch_info is None or not protected_branch_info.get("last_commit", {}).get("sha"):
            raise IntegrationError("Add a separate protected non-default branch so both protected-branch checks can run.")

        print("\nLIVE WRITE PLAN")
        print(f"  Gitea instance : {instance}")
        print(f"  Test repository: {owner}/{repo}")
        print(f"  Write PAT      : {test_account} (previously verified non-admin account)")
        print("  Read path      : configured Gitea AdminPat; this test uses it only for read API calls")
        print("  Write scope    : write:repository; bot account access is limited to this test repository")
        print(f"  Base branch    : {base_branch}")
        print(f"  Protected test : also attempts a direct write to {protected_branch_info['name']}")
        print(f"  Working branch : {branch}")
        print(f"  Read-only test : attempts to create {read_only_branch}; expected to stop at the MCP write:gitea check")
        print(f"  Allowlist test : attempts repo {owner}/{unallowlisted_repo}; expected to stop before Gitea API access")
        print(f"  Single-file API: upsert_file creates {standalone_path}; delete_file removes it in a separate commit")
        print(f"    content = {standalone_text.rstrip()!r}")
        print("  Commit 1       : create these two text files atomically:")
        print(f"    {created_path} = {initial_text.rstrip()!r}")
        print(f"    {deleted_path} = {delete_text.rstrip()!r}")
        print("  Commit 2       : update created.txt and delete delete-me.txt in one batch")
        print(f"    {created_path} = {updated_text.rstrip()!r}")
        print("  Pull request   : open a PR from the working branch to the default branch")
        print(f"    title = {pr_title!r}")
        print(f"    body  = {pr_body!r}")
        print(f"  Stale SHA test : attempts to update {created_path} to 'stale\\n' using the old blob SHA")
        print(f"  Stale HEAD test: attempts to create {file_root}/stale-head.txt with 'stale\\n' on {branch}")
        print(f"  Default test   : attempts a direct write to the default branch {base_branch}")
        print(f"  Protected test : attempts a direct write to protected branch {protected_branch_info['name']}")
        print(f"  Atomicity test : a stale-SHA batch also attempts to create {atomic_probe_path}; neither change may appear")
        print("  Side effects   : leaves the test branch and PR for review; no repo/branch/data cleanup is performed.")
        print("  Failure bound  : both listed protected branches must remain protected and the test account must not bypass protection.")
        print("  Internal refs  : the service creates unique mcp-tmp-* staging refs and deletes them after CAS.")
        confirmation = f"WRITE {owner}/{repo} {branch}"
        if input(f"Type {confirmation} to continue: ").strip() != confirmation:
            print("Cancelled before any remote write.")
            return 2
        write_authorized = True

        phase = "create working branch"
        branch_result = require_object(session.call("create_branch", {
            "owner": owner, "repo": repo, "newBranch": branch, "fromRef": base_branch,
        }), "create_branch")
        branch_created = True
        expected_head = branch_result.get("sha")
        if not expected_head:
            raise IntegrationError("create_branch returned no starting HEAD SHA.")
        last_known_head = expected_head
        created_branch_info = next(
            (item for item in session.call("list_branches", {"owner": owner, "repo": repo}) if item.get("name") == branch),
            None,
        )
        if created_branch_info is None or created_branch_info.get("protected") is not False:
            raise IntegrationError("The new working branch is missing or protected; it must be unprotected for commits.")

        phase = "upsert_file create"
        single_create = require_object(session.call("upsert_file", {
            "owner": owner, "repo": repo, "path": standalone_path, "content": standalone_text,
            "branch": branch, "expectedHeadSha": last_known_head,
            "message": f"test: exercise upsert_file {run_id}",
        }), "upsert_file create")
        last_known_head = single_create.get("commit_sha")
        if not last_known_head:
            raise IntegrationError("upsert_file returned no commit_sha.")

        phase = "delete_file"
        single_tree = require_object(session.call("list_tree", {
            "owner": owner, "repo": repo, "ref": branch, "recursive": True,
        }), "list_tree before delete_file")
        single_entries = {entry.get("path"): entry for entry in single_tree.get("entries", [])}
        if standalone_path not in single_entries:
            raise IntegrationError("upsert_file did not create the standalone test file.")
        single_delete = require_object(session.call("delete_file", {
            "owner": owner, "repo": repo, "path": standalone_path,
            "expectedSha": single_entries[standalone_path].get("sha"),
            "branch": branch, "expectedHeadSha": last_known_head,
            "message": f"test: exercise delete_file {run_id}",
        }), "delete_file")
        last_known_head = single_delete.get("commit_sha")
        if not last_known_head:
            raise IntegrationError("delete_file returned no commit_sha.")

        phase = "commit_files create batch"
        first_commit = require_object(session.call("commit_files", {
            "owner": owner,
            "repo": repo,
            "branch": branch,
            "expectedHeadSha": last_known_head,
            "message": f"test: create integration fixtures {run_id}",
            "changes": [
                {"operation": "create", "path": created_path, "content": initial_text},
                {"operation": "create", "path": deleted_path, "content": delete_text},
            ],
        }), "commit_files create batch")
        first_head = first_commit.get("commit_sha")
        if not first_head:
            raise IntegrationError("First commit returned no commit_sha.")
        last_known_head = first_head

        tree = require_object(session.call("list_tree", {
            "owner": owner, "repo": repo, "ref": branch, "recursive": True,
        }), "list_tree")
        entries = {entry.get("path"): entry for entry in tree.get("entries", [])}
        if created_path not in entries or deleted_path not in entries:
            raise IntegrationError("The first batch did not create both fixture files.")
        current_text = require_object(session.call("read_file", {
            "owner": owner, "repo": repo, "path": created_path, "ref": branch,
        }), "read_file")
        if current_text.get("content") != initial_text:
            raise IntegrationError("read_file did not return the expected fixture content.")

        phase = "commit_files update/delete batch"
        second_commit = require_object(session.call("commit_files", {
            "owner": owner,
            "repo": repo,
            "branch": branch,
            "expectedHeadSha": first_head,
            "message": f"test: update and delete integration fixtures {run_id}",
            "changes": [
                {"operation": "update", "path": created_path, "content": updated_text, "expectedSha": entries[created_path].get("sha")},
                {"operation": "delete", "path": deleted_path, "expectedSha": entries[deleted_path].get("sha")},
            ],
        }), "commit_files update/delete batch")
        second_head = second_commit.get("commit_sha")
        if not second_head:
            raise IntegrationError("Second commit returned no commit_sha.")
        last_known_head = second_head
        if second_commit.get("cleanup_warning"):
            raise IntegrationError(f"The staging ref cleanup needs review: {second_commit['cleanup_warning']}")

        phase = "verify final remote file state"
        final_tree = require_object(session.call("list_tree", {
            "owner": owner, "repo": repo, "ref": branch, "recursive": True,
        }), "list_tree after commit_files")
        final_entries = {entry.get("path"): entry for entry in final_tree.get("entries", [])}
        if created_path not in final_entries or deleted_path in final_entries or standalone_path in final_entries:
            raise IntegrationError("Final remote tree does not match the expected create/update/delete state.")
        final_text = require_object(session.call("read_file", {
            "owner": owner, "repo": repo, "path": created_path, "ref": branch,
        }), "read_file after commit_files")
        if final_text.get("content") != updated_text:
            raise IntegrationError("The final remote file content does not match the expected update.")

        commit_diff = require_object(session.call("get_diff", {
            "owner": owner, "repo": repo, "commitSha": second_head,
        }), "get_diff commit")
        if created_path not in commit_diff.get("diff", "") or deleted_path not in commit_diff.get("diff", ""):
            raise IntegrationError("The second commit diff did not include both expected paths.")
        compare_diff = require_object(session.call("get_diff", {
            "owner": owner, "repo": repo, "baseBranch": base_branch, "headBranch": branch,
        }), "get_diff compare")
        if created_path not in compare_diff.get("diff", ""):
            raise IntegrationError("The branch comparison did not include the created fixture file.")

        phase = "create_pull_request"
        pull = require_object(session.call("create_pull_request", {
            "owner": owner,
            "repo": repo,
            "head": branch,
            "baseBranch": base_branch,
            "title": pr_title,
            "body": pr_body,
        }), "create_pull_request")
        number = pull.get("number")
        if not isinstance(number, int):
            raise IntegrationError("create_pull_request returned no numeric PR number.")
        pull_number = number
        phase = "read_pull"
        read_pull = require_object(session.call("read_pull", {
            "owner": owner, "repo": repo, "number": number,
        }), "read_pull")
        changed_paths = {file.get("filename") for file in read_pull.get("changed_files", [])}
        if (run_id not in (read_pull.get("body") or "") or
                read_pull.get("title") != pr_title or read_pull.get("head") != branch or
                read_pull.get("base_branch") != base_branch or
                changed_paths != {created_path}):
            raise IntegrationError("read_pull did not return the expected PR title, body, branches, and changed files.")

        phase = "atomic stale-SHA rejection"
        session.expect_error("commit_files", {
            "owner": owner, "repo": repo, "branch": branch, "expectedHeadSha": second_head,
            "message": f"test: stale file SHA {run_id}",
            "changes": [
                {"operation": "update", "path": created_path, "content": "stale\n", "expectedSha": entries[created_path].get("sha")},
                {"operation": "create", "path": atomic_probe_path, "content": "must not partially appear\n"},
            ],
        }, "SHA conflict")
        after_failed_batch = require_object(session.call("list_tree", {
            "owner": owner, "repo": repo, "ref": branch, "recursive": True,
        }), "list_tree after rejected batch")
        after_failed_entries = {entry.get("path"): entry for entry in after_failed_batch.get("entries", [])}
        after_failed_text = require_object(session.call("read_file", {
            "owner": owner, "repo": repo, "path": created_path, "ref": branch,
        }), "read_file after rejected batch")
        current_branch_head = next(
            (item.get("last_commit", {}).get("sha") for item in session.call("list_branches", {"owner": owner, "repo": repo}) if item.get("name") == branch),
            None,
        )
        if (atomic_probe_path in after_failed_entries or
                after_failed_text.get("content") != updated_text or current_branch_head != second_head):
            raise IntegrationError("Rejected multi-file commit partially changed the remote branch.")

        phase = "stale branch HEAD rejection"
        session.expect_error("commit_files", {
            "owner": owner, "repo": repo, "branch": branch, "expectedHeadSha": first_head,
            "message": f"test: stale branch HEAD {run_id}",
            "changes": [{"operation": "create", "path": f"{file_root}/stale-head.txt", "content": "stale\n"}],
        }, "Branch HEAD conflict")
        after_stale_head_tree = require_object(session.call("list_tree", {
            "owner": owner, "repo": repo, "ref": branch, "recursive": True,
        }), "list_tree after stale HEAD rejection")
        after_stale_head_paths = {entry.get("path") for entry in after_stale_head_tree.get("entries", [])}
        after_stale_head = next(
            (item.get("last_commit", {}).get("sha") for item in session.call("list_branches", {"owner": owner, "repo": repo}) if item.get("name") == branch),
            None,
        )
        if after_stale_head != second_head or f"{file_root}/stale-head.txt" in after_stale_head_paths:
            raise IntegrationError("Stale expectedHeadSha changed the remote branch.")

        phase = "default branch rejection"
        session.expect_error_any("commit_files", {
            "owner": owner, "repo": repo, "branch": base_branch,
            "expectedHeadSha": base_info["last_commit"]["sha"],
            "message": f"test: default branch must reject {run_id}",
            "changes": [{"operation": "create", "path": f"{file_root}/default-branch-blocked.txt", "content": "blocked\n"}],
        }, ("default branch", "protected branch"))

        phase = "protected non-default branch rejection"
        session.expect_error("commit_files", {
            "owner": owner, "repo": repo, "branch": protected_branch_info["name"],
            "expectedHeadSha": protected_branch_info["last_commit"]["sha"],
            "message": f"test: protected branch must reject {run_id}",
            "changes": [{"operation": "create", "path": f"{file_root}/protected-branch-blocked.txt", "content": "blocked\n"}],
        }, "protected branch")

        phase = "non-allowlisted repository rejection"
        session.expect_error("create_branch", {
            "owner": owner, "repo": unallowlisted_repo,
            "newBranch": f"feature/not-allowed-{run_id}", "fromRef": base_branch,
        }, "allowlist")

        phase = "read-only JWT rejection"
        read_only = McpSession(local_endpoint, read_token)
        read_only.initialize()
        read_only.expect_error("create_branch", {
            "owner": owner, "repo": repo,
            "newBranch": read_only_branch, "fromRef": base_branch,
        }, "write:gitea")

        print("\nPASS: branch creation, single-file upsert/delete, atomic multi-file create/update/delete, final remote state, stale file SHA/HEAD conflicts, default/protected branch rejection, diff, PR creation/read, allowlist rejection, and read-only JWT rejection.")
        print(f"Test repository: {owner}/{repo}")
        print(f"Working branch : {branch}")
        print(f"PR             : {pull.get('html_url')} (#{number})")
        print("The branch and PR are intentionally left in the test repository for review.")
        return 0
    except IntegrationError:
        if write_authorized:
            print("\nRECOVERY CHECK", file=sys.stderr)
            print(f"  Repository    : {owner}/{repo}", file=sys.stderr)
            print(f"  Run ID        : {run_id}", file=sys.stderr)
            print(f"  Last phase    : {phase}", file=sys.stderr)
            print(f"  Working branch: {branch} (created={branch_created})", file=sys.stderr)
            print(f"  Last known HEAD: {last_known_head or 'unknown; inspect list_branches/get_diff before retrying'}", file=sys.stderr)
            print(f"  PR number     : {pull_number or 'unknown; inspect open PRs by head branch before retrying'}", file=sys.stderr)
            print("  No test branch, PR, or file data was automatically deleted.", file=sys.stderr)
        raise
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (IntegrationError, ToolFailure) as error:
        print(f"INTEGRATION TEST NOT COMPLETED: {error}", file=sys.stderr)
        raise SystemExit(1)

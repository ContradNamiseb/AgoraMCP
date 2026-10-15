"""Two-client live acceptance test for AgoraMCP.

Drives real `agora serve` processes over stdio JSON-RPC against one shared
database file and walks the acceptance sequence:

  1. A posts with wait=true, B reviews from a second process -> A gets `answered`.
  2. B beats offline (clean shutdown), A posts again -> `no_peer_online` fast,
     roster echo contains A itself.
  3. Only online peer exists under a DIFFERENT specialty -> `no_peer_online`
     carrying that peer in the roster (substitution path).
  4. A peer is online but never reviews -> `timeout` at ~5s, never a hang.
  5. Directed question: visible to the target's fetch, marked for others.

Usage:  python tools/livetest.py [path\to\agora.exe]
"""

import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time

EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(__file__), "..", "publish", "agora.exe")
EXE = os.path.abspath(EXE)

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  [{detail}]" if detail else ""))


class Client:
    """One MCP client: one agora serve process, JSON-RPC over stdio."""

    def __init__(self, name, db_path):
        self.proc = subprocess.Popen(
            [EXE, "serve", "--db", db_path],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", bufsize=1)
        self.next_id = 0
        self.call("initialize", protocolVersion="2024-11-05", capabilities={},
                  clientInfo={"name": name, "version": "0"})
        self.call("notifications/initialized")  # notification: no response

    def call(self, method, **params):
        self.next_id += 1
        request = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        if request["method"].startswith("notifications/"):
            return None
        deadline = time.time() + 90
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError(f"{method}: server closed the stream")
            message = json.loads(line)
            if message.get("id") == self.next_id:
                if "error" in message:
                    raise RuntimeError(f"{method}: {message['error']}")
                return message["result"]
        raise TimeoutError(method)

    def tool(self, name, **args):
        result = self.call("tools/call", name=name, arguments=args)
        return result["content"][0]["text"]

    def close(self):
        self.proc.stdin.close()
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def main():
    db_path = os.path.join(tempfile.mkdtemp(prefix="agora-live-"), "agora.db")
    print(f"shared db: {db_path}\n")

    a = Client("poster", db_path)
    try:
        # ---- 1. post (blocking) + concurrent peer review -> answered ----
        a.tool("heartbeat", agent="claude-test", specialty="kernel",
               capabilities="Claude Opus, high-level design")
        b = Client("reviewer", db_path)
        b.tool("heartbeat", agent="zcode-test", specialty="kernel",
               capabilities="GLM-5.3, Triton/CUDA")

        roster = a.tool("who_is_online")
        check("1a. both agents visible in roster",
              "claude-test" in roster and "zcode-test" in roster)

        answer_holder = {}

        def post_and_wait():
            answer_holder["text"] = a.tool(
                "post_question", topic="16-token warp scan layout",
                context="Triton, H100, need the canonical layout",
                author="claude-test", specialty="kernel", wait=True, timeout_seconds=30)

        started = time.time()
        waiter = threading.Thread(target=post_and_wait)
        waiter.start()

        time.sleep(2)  # let the question land and show up in the reviewer's inbox
        inbox = b.tool("fetch_pending_queries", agent="zcode-test", specialty="kernel")
        check("1b. reviewer's inbox shows the question", "warp scan layout" in inbox,
              inbox.splitlines()[0] if inbox else "")
        thread_id = re.search(r"#(\d+)", inbox).group(1)
        b.tool("submit_peer_review", thread_id=int(thread_id),
               body="use tl::cumsum along axis=0; layout [lane, warp]", author="zcode-test")

        waiter.join(60)
        waited = time.time() - started
        answer = answer_holder.get("text", "")
        check("1c. wait returns answered with the review",
              "status: answered" in answer and "tl::cumsum" in answer, f"{waited:.1f}s")

        after = b.tool("fetch_pending_queries", agent="zcode-test", specialty="kernel")
        check("1d. answered thread left the pending inbox", "none" in after.lower())

        # ---- 2. peer gone (offline beat, then shutdown) -> fast no_peer_online ----
        b.tool("heartbeat", agent="zcode-test", offline=True)
        b.close()
        time.sleep(0.5)

        started = time.time()
        answer = a.tool("post_question", topic="nobody home", author="claude-test",
                        wait=True, timeout_seconds=10)
        elapsed = time.time() - started
        check("2a. no_peer_online after peer left", "status: no_peer_online" in answer)
        check("2b. negative path carries the self roster echo", "claude-test" in answer)
        check("2c. fast-fail, never a hang", elapsed < 2, f"{elapsed:.1f}s")

        # ---- 3. specialty mismatch -> roster names the substitute ----
        c = Client("substitute", db_path)
        c.tool("heartbeat", agent="gemini-test", specialty="research",
               capabilities="Gemini, deep context retrieval")

        answer = a.tool("post_question", topic="kernel question", author="claude-test",
                        specialty="kernel", wait=True, timeout_seconds=10)
        check("3. no kernel peer -> roster shows gemini-test as substitute",
              "status: no_peer_online" in answer and "gemini-test" in answer)

        # ---- 4. online peer never reviews -> timeout at ~deadline ----
        started = time.time()
        answer = a.tool("post_question", topic="deep research question",
                        author="claude-test", specialty="research",
                        wait=True, timeout_seconds=5)
        elapsed = time.time() - started
        check("4a. timeout terminal", "status: timeout" in answer)
        check("4b. ended near the 5s deadline", 4.0 <= elapsed < 15, f"{elapsed:.1f}s")

        # ---- 5. directed question: target sees it, others see a mark ----
        posted = a.tool("post_question", topic="for gemini specifically",
                        author="claude-test", target_agent="gemini-test")
        check("5a. directed post returns thread id", "thread" in posted.lower())

        gemini_inbox = c.tool("fetch_pending_queries", agent="gemini-test")
        check("5b. target sees the directed thread",
              "for gemini specifically" in gemini_inbox)

        others = a.tool("fetch_pending_queries", agent="claude-test")
        check("5c. others see it marked (dir->gemini-test)",
              "dir\u2192gemini-test" in others)

        c.close()
    finally:
        a.close()

    print(f"\n{len(PASS)} passed, {len(FAIL)} failed")
    if FAIL:
        print("failed:", ", ".join(FAIL))
        sys.exit(1)


if __name__ == "__main__":
    main()

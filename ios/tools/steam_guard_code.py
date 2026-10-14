#!/usr/bin/env python3
"""Gets a Steam Guard code for a CI sign-in through a GitHub issue.

Steam blocks approving a sign-in in the Steam app when it comes from far away
(GitHub's runners are in the US/UK), but it accepts a Steam Guard code. This
opens an issue that @mentions whoever started the run and waits for them to
reply with the code from the Steam app (Steam Guard tab) or the e-mail. Only
that person's replies are read, and each one is deleted once read.

  steam_guard_code.py device|email:ADDRESS first|retry
      SteamFetch's --code-command. Prints the code on stdout.
  steam_guard_code.py --close [NOTE]
      Closes the issue, if one was opened.

Env: GITHUB_TOKEN (issues: write), GITHUB_REPOSITORY, GITHUB_SERVER_URL,
GITHUB_API_URL, GITHUB_RUN_ID, GITHUB_RUN_NUMBER, RUNNER_TEMP, and
GUARD_USER, the GitHub login allowed to answer.
"""

import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

WAIT_SECONDS = 15 * 60
POLL_SECONDS = 2
# A reply that is just the code, in any case, or a 5-character upper-case
# word in a longer reply ("code 7KX2P").
CODE_ONLY = re.compile(r"^[\s`'\".:]*([A-Za-z0-9]{5})[\s`'\".:]*$")
CODE_IN_TEXT = re.compile(r"\b([A-Z0-9]{5})\b")

REPO = os.environ["GITHUB_REPOSITORY"]
API = os.environ.get("GITHUB_API_URL", "https://api.github.com")
STATE = os.path.join(os.environ.get("RUNNER_TEMP", "/tmp"), "steam-guard-issue.json")


def log(message):
    print(message, file=sys.stderr, flush=True)


def call(method, path, body=None):
    request = urllib.request.Request(
        f"{API}/repos/{REPO}{path}",
        method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={
            "Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read()
        return json.loads(data) if data else None


def load_state():
    try:
        with open(STATE) as f:
            return json.load(f)
    except FileNotFoundError:
        return {}


def save_state(state):
    with open(STATE, "w") as f:
        json.dump(state, f)


def open_issue(kind, user):
    run_url = f"{os.environ['GITHUB_SERVER_URL']}/{REPO}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
    where = (
        f"the code Steam just e-mailed to {kind[6:]}"
        if kind.startswith("email:")
        else "the 5-character code in the Steam app (**Steam Guard** tab, the shield icon)"
    )
    body = (
        f"@{user} the [iOS game build #{os.environ['GITHUB_RUN_NUMBER']}]({run_url}) "
        f"needs a Steam Guard code to sign in to Steam.\n\n"
        f"**Reply to this issue with {where}.** "
        "App codes change every 30 seconds, so send it right after it changes.\n\n"
        + (
            ""
            if kind.startswith("email:")
            else "No Steam Guard in the Steam app? Reply `email` and Steam will e-mail you a code to send here.\n\n"
        )
        +
        "After you reply, the Steam app may show a sign-in request from the build machine. "
        "**Don't approve it**: Steam blocks approving from that far away, and your code already signs the build in.\n\n"
        "Only your replies are read, and each one is deleted after it's read. "
        "This issue closes when the build is signed in. "
        f"The build waits {WAIT_SECONDS // 60} minutes for a code."
    )
    try:
        issue = call("POST", "/issues", {"title": f"Steam Guard code for iOS build #{os.environ['GITHUB_RUN_NUMBER']}", "body": body})
    except urllib.error.HTTPError as e:
        if e.code in (403, 404, 410):
            log(
                "::error::Couldn't open an issue to ask for the Steam Guard code "
                f"(HTTP {e.code}). Turn on Issues in the repo's Settings > General > Features, "
                "or add the STEAM_REFRESH_TOKEN secret (see ios/README.md)."
            )
            sys.exit(2)
        raise
    return issue


def request_code(kind, attempt):
    user = os.environ["GUARD_USER"]
    state = load_state()
    if "issue" not in state:
        issue = open_issue(kind, user)
        state = {"issue": issue["number"], "url": issue["html_url"], "seen": 0}
        save_state(state)
    elif attempt == "retry":
        call("POST", f"/issues/{state['issue']}/comments",
             {"body": f"@{user} Steam rejected that code (wrong or expired). Reply with the current one."})
    elif kind.startswith("email:"):
        call("POST", f"/issues/{state['issue']}/comments",
             {"body": f"@{user} Steam just e-mailed a code to {kind[6:]}. Reply with it."})
    log(f"::notice::Reply to {state['url']} with your Steam Guard code. Waiting up to {WAIT_SECONDS // 60} minutes.")

    deadline = time.time() + WAIT_SECONDS
    while time.time() < deadline:
        try:
            comments = call("GET", f"/issues/{state['issue']}/comments?per_page=100")
        except (urllib.error.URLError, TimeoutError) as e:
            log(f"(polling the issue failed, retrying: {e})")
            comments = []
        for comment in comments:
            if comment["id"] <= state["seen"] or comment["user"]["login"].lower() != user.lower():
                continue
            state["seen"] = comment["id"]
            save_state(state)
            try:
                call("DELETE", f"/issues/comments/{comment['id']}")
            except urllib.error.URLError:
                pass
            text = comment["body"] or ""
            match = CODE_ONLY.match(text) or CODE_IN_TEXT.search(text)
            if match:
                log("Got a code from the issue.")
                print(match.group(1).upper())
                return
            call("POST", f"/issues/{state['issue']}/comments",
                 {"body": f"@{user} that reply had no 5-character code in it. Reply with just the code."})
        time.sleep(POLL_SECONDS)
    log("::error::No Steam Guard code arrived in time.")
    sys.exit(1)


def close(note):
    state = load_state()
    if "issue" not in state:
        return
    try:
        if note:
            call("POST", f"/issues/{state['issue']}/comments", {"body": note})
        call("PATCH", f"/issues/{state['issue']}", {"state": "closed", "state_reason": "completed"})
    except urllib.error.URLError as e:
        log(f"(couldn't close issue #{state['issue']}: {e})")


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "--close":
        close(" ".join(sys.argv[2:]))
    elif len(sys.argv) == 3:
        request_code(sys.argv[1], sys.argv[2])
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()

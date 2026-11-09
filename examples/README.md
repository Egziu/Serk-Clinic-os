# Optional hook integration — review before enabling

Claude Code supports lifecycle hooks, but automatic scripts must be reviewed per OS and client version. **No hook is enabled by this bundle.**

Example: put the `PostToolUse` entry below into a reviewed `.claude/settings.json` and create an OS-compatible script that runs a fast read-only syntax check. Be cautious with shell quoting on Windows. Do not let hooks silently modify protected files or leak PHI.

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          { "type": "command", "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/git-diff-check.sh" }
        ]
      }
    ]
  }
}
```

If enabled, run `claude /doctor` if supported and verify hook behavior on synthetic changes only. The check can miss files written via Bash or outside Claude. Mandatory full checks must run in CI independently of hooks.

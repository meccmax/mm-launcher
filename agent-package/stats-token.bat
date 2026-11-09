@echo off
REM ===================================================================
REM  Makes the password the MM website uses to download MM BlackBox stats
REM  (for the leaderboards on misfitmercenaries.com), saves it in agent.jsonc
REM  and restarts the agent service. Copy the password it shows into the
REM  website repo's BLACKBOX_TOKEN secret on GitHub.
REM  Shows the existing password if one is already set. To replace it, run:
REM      MMServerAgent.exe stats-token --new
REM  Right-click and choose "Run as administrator".
REM ===================================================================
cd /d "%~dp0"
MMServerAgent.exe stats-token %*

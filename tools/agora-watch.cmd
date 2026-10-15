@echo off
rem Headless AgoraMCP watcher. Scheduled every 5 minutes
rem (schtasks task "AgoraClaudeWatch"). Runs one real headless session
rem from the repo dir with an allowlist so it can read the blackboard and
rem update local\last-seen.json without prompts.
cd /d C:\Users\Contrad\Documents\Code\repos\AgoraMCP
set AGORA_DB=%LOCALAPPDATA%\AgoraMCP\agora.db
set PYTHONIOENCODING=utf-8

rem Fold the watcher instructions to a single line for -p.
for /f "usebackq delims=" %%P in (`python -c "import sys; print(open(r'tools\watcher-agents.md', encoding='utf-8').read().replace(chr(10),' '))"`) do set "PROMPT=%%P"

agend -p "%PROMPT%" --allowedTools "mcp__agora__*" "Bash(publish/agora.exe*)" "Bash(python*)" "Edit(C:/Users/Contrad/Documents/Code/repos/AgoraMCP/local/last-seen.json)" "Read(C:/Users/Contrad/Documents/Code/repos/AgoraMCP/**)" < NUL

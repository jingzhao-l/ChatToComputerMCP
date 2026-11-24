# Independent task workers

Enable **Keep running commands through app restarts** in Security before
connecting. It is off by default. Unlike ordinary private command receipts,
this mode stores bounded output (up to 256 chunks / 256 KiB per job) and
local metadata, which may contain sensitive content. The command body itself
is sent through the worker stdin, not written into the worker record.
The separate command-history content preference is still independent.

A worker is a distinct process launched by the local native application.
It owns the child process, timeout, output and cancellation marker. On
gateway restart, the new gateway checks the worker status and exclusive
worker lock, the original request receipt and owner, the canonical workspace
and current shell execution mode. Recovered active jobs occupy the shared
capacity budget and retain their original session ID. Repeat the same
request ID only for the same command; a different command is rejected.

Use list_my_commands to find sessions and read_command_output with the
original session ID to resume reading; cancel_command targets only that job.
The command_receipt_status response reports reattachable only for a live
attached worker. An explicit Disconnect cancels tasks; graceful application
quit preserves launched independent workers. Reconnect with the same profile.
A queued job that has not launched is not replayed after process death.

Changing a workspace, permission or client grant does not authorize an old
job to continue when reattached. Unapproved live workers are cancelled
before the new runtime accepts requests. Shared connection credentials
remain a shared principal; they cannot identify individual conversations.

Worker death, OS shutdown and reboot do not constitute successful live
reattachment. Jobs with no demonstrably live worker are interrupted, not
replayed; inspect external effects before starting another command. In
particular, an unprivileged host shell may have escaped process-group
management, so an interrupted record is not proof that all external effects
or descendants have stopped. Docker mode is separate and requires a locally
approved image and installed engine.

Mac regression kills an actual fixture gateway PID and verifies worker
survival, original session identity, no duplicate execution, final output,
terminal receipt and targeted cancellation. Windows equivalent regression is included in the native Windows x64 suite; hosted CI has executed it. A Windows-targeting compile alone is not native execution evidence. Physical Windows and reboot/ARM64 scenarios remain separate.

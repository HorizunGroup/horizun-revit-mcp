# Automatic notice closure

The default remains unchanged: while an MCP call waits for Revit, the bridge may
close the save reminder with Cancel and External Tool Failure with Close. Other
dialogs remain blocked and are reported to the caller. Each automatic closure is
recorded in `modal_dialogs_closed`.

The owner may set `"auto_close_external_tool_failure": false` in
`%USERPROFILE%\.horizun\settings.json`. This is re-read on every use. A malformed
settings file also disables closure of External Tool Failure.

The save reminder has its own owner setting, `"auto_close_save_reminder": false`,
and its own Revit session override, `HORIZUN_AUTO_CLOSE_SAVE_REMINDER=false` (or
`0`). Either leaves the reminder open and reports it. This does not disable
External Tool Failure closure. The default for each independent policy is true;
a malformed settings file disables both. No automatic response saves a model.

For one Revit process session, launch Revit with the environment variable
`HORIZUN_AUTO_CLOSE_EXTERNAL_TOOL_FAILURE=false` (or `0`). This can disable
closure but cannot override an owner's false setting. To prohibit **both**
automatic notice closures for the session, use `HORIZUN_AUTO_CLOSE_DIALOGS=false`
(or `0`) instead. Set the environment before starting Revit; changing a client's
environment after Revit starts does not change that Revit process.

`horizun_health.modal_dialog_policy` always reports the effective policy when
Revit answers. If Revit cannot answer, the effective flags are `null` with a
reason: the server cannot infer another process's session environment. Disabling
closure leaves the notice open and returns its identity through the existing
modal-dialog blocking response.

Health names both effective policies, their defaults, owner settings and session
environment variables. `save_reminder_auto_close` remains as a compatibility
alias of `auto_close_save_reminder`; neither flag describes a save operation.

These controls apply both while the dispatcher waits and inside a running command's
`DialogBoxShowing` watcher, including Python and document-open calls. A disabled
notice policy leaves that notice open even inside an `on_open_dialog=dismiss`
scope. Its identity and the fact it was left open are recorded in `revit_said`.
The owner-only permission question always remains for the person to answer.

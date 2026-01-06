# razlike.md Note – Implementation Check

Source: `docs/razlike.md`

Findings
- File only contains example strings (`charging format: cp/ACE0748001?connectorId=1` and notice text). No actionable requirements are specified.
- Current public start routing expects query parameters `cp` and `conn` (`/Public/Start?cp=...&conn=...`); the path-style `cp/ACE0748001?connectorId=1` shown in the note is **not** implemented in controllers or routing.

Status
- No code changes required unless path-style QR links are needed; would require a route that maps `/cp/{id}` to `PublicController.Start` and a matching query/route parameter for `connectorId`.

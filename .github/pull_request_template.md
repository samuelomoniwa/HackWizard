## What does this change?

- [ ] Rules (`Rules/kb/*.json`) — linked to a Dortania/upstream source or a hardware report issue
- [ ] Code

## Checklist
- [ ] `python tools/kb_tool.py lint` passes
- [ ] Added/updated a profile in `tests/HackWizard.Tests/Fixtures/Profiles/` and ran `python tools/kb_tool.py expect`
- [ ] Ran `python tools/kb_tool.py manifest`
- [ ] `./build.ps1` passes (golden snapshots re-approved only if the config change is intended)

# CXP_NAME

A cxagent plugin with one tool, `CXP_NAME_greet`, which greets someone by name. It asks before
greeting anyone called `admin` — a stand-in for the per-call permission a real tool needs.

## Develop

```
dotnet build
cxagent plugin check bin/Debug/net10.0
cxagent plugin call bin/Debug/net10.0 CXP_NAME_greet '{"name":"Ada"}'
dotnet test tests
dotnet build -c Release && cxagent plugin pack bin/Release/net10.0
```

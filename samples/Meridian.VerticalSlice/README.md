# Vertical Slice Sample

This sample demonstrates **one file per feature** style:

- queries, handlers, validators, notifications, and DTOs are grouped together by feature
- every use case owns its request/response type and behavior
- shared pipeline behavior is still `IMediator`
- stream request demonstrates slice-local activity feeds

Run:

```bash
dotnet run --project samples/Meridian.VerticalSlice/Meridian.VerticalSlice.csproj -c Release
```

This sample focuses on the architectural pattern. For features added in
Meridian v1.1 — `MaxDepth` / `MaxCollectionItems` safety defaults,
`[GenerateMapper]` source generator, `TurkishCulture` helpers,
`AddMeridianStandard`, `AuditBehavior`, and `LocalizedValidationBehavior`
— see [`Meridian.QuickStart`](../Meridian.QuickStart/) and the
`Safety defaults + culture (v1.1)` demo in
[`Meridian.Showcase`](../Meridian.Showcase/).

# Meridian Showcase

Run the sample from the repository root:

```bash
dotnet run --project samples/Meridian.Showcase
```

The sample is split into five demos:

- `Mapping`: profiles, `AddProfiles`, `ForAllOtherMembers`, `IMemberValueResolver`, `IncludeMembers`, `ReverseMap`, and `ProjectTo`
- `Mediator Query`: validation, logging, retry, caching, and correlation
- `Mediator Command`: validation, logging, transaction, authorization, idempotency, and cache invalidation
- `Notifications`: `NotificationPublisherType` with `ResilientTaskWhenAllPublisher`
- `Streaming`: open stream behavior registration with `AddOpenStreamBehavior`

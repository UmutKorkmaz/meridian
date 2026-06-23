## 2024-06-06 - [Avoid LINQ `Reverse().Aggregate()` on pipeline execution hot paths]
**Learning:** In the Mediator pipeline execution (both request and streaming), `behaviors.Reverse().Aggregate(...)` allocated significant memory per dispatch due to intermediate enumerators and delegates, representing a hidden allocation cost. Since Dependency Injection containers almost exclusively return an array or `List<T>` for multiple registrations, casting directly to an `IList<T>` allows for zero-allocation backward traversal.
**Action:** Replace `Reverse().Aggregate(...)` with an `IList` reverse for-loop for chained delegate construction in hot paths. Keep the LINQ method as a fallback only when the collection is not an `IList`.

## 2026-06-03 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2024-06-07 - [Fast Path for Mediator Publishing]
**Learning:** Checking for an empty `ICollection` or `IReadOnlyCollection` and early-returning in mediator pipelines saves list allocations and iterator overhead, which has measurable impact at scale.
**Action:** Always add early exits for zero-handler scenarios when writing mediator components.

## 2024-06-08 - Optimize Pipeline Construction with IList For-loop
**Learning:** LINQ methods like `Reverse()` and `Aggregate()` introduce notable overhead through enumerator allocations and delegate creations when composing handler pipelines. Since `Microsoft.Extensions.DependencyInjection` returns arrays (which implement `IList<T>`) for multiple registrations via `GetServices<T>()`, iterating them in reverse with a `for` loop avoids these allocations.
**Action:** When building nested delegate chains or pipelines from an `IEnumerable<T>`, check if it implements `IList<T>` first to use an allocation-free reverse `for` loop instead of `.Reverse().Aggregate()`.

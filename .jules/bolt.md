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

## 2025-06-10 - Mediator LINQ Allocations
**Learning:** In highly-executed CQRS/Mediator pipelines, using LINQ operations like `.Reverse().Aggregate(...)` for pipeline assembly or `.Select().ToList()` for handler dispatch introduces significant per-request heap allocations due to delegate captures and enumerable state machines. Microsoft.Extensions.DependencyInjection natively returns `T[]` for `IEnumerable<T>`, which provides an opportunity for array-based optimizations.
**Action:** Replace LINQ pipeline assembly in hot paths with backwards `for` loops against pre-sized arrays/collections. Type-check `IEnumerable<T>` for `T[]` or `ICollection<T>` from DI containers to enable zero-allocation enumeration and pipeline construction.

## 2025-03-03 - Avoiding LINQ Allocations in .NET DI Pipelines
**Learning:** `IServiceProvider.GetServices<T>()` typically returns an array (`T[]`). Using LINQ extensions like `.Reverse().Aggregate()` or `.Select().ToList()` on the returned `IEnumerable<T>` creates hidden enumerator and closure allocations per request.
**Action:** Always type-check `IEnumerable<T>` against `IList<T>` or `ICollection<T>` when resolving multiple services. Iterate using `for` loops (for backwards iteration) or pre-allocated `List<T>` to avoid LINQ overhead and achieve zero-allocation pipeline construction.

## 2025-03-03 - Avoid LINQ allocations in pipeline construction
**Learning:** Microsoft.Extensions.DependencyInjection returns arrays (`T[]`) for `GetServices<T>()`. Using LINQ methods like `.Reverse()` and `.Aggregate()` on this output creates unnecessary per-request heap allocations for enumerators, delegates, and array copies.
**Action:** Always type-check `IEnumerable<T>` for `IList<T>` (or `ICollection<T>`). Use backward `for` loops (for reverse order) or forward loops and pre-sized lists to build pipelines and avoid LINQ allocations in the hot path.

## 2026-06-14 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** In hot paths (like building request or stream pipelines from DI behaviors), using LINQ `.Reverse().Aggregate()` forces multiple enumerator and delegate allocations per request.
**Action:** MS.DI typically returns `IEnumerable<T>` as arrays or lists. Always check the resolved enumerable for `IList<T>` or `IReadOnlyList<T>` and construct the pipeline explicitly using a backward `for` loop to achieve zero-allocation pipeline construction.

## 2026-06-03 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** Constructing pipeline behaviors using `.Reverse().Aggregate()` forces enumerator allocation and delegate creation via LINQ.
**Action:** Always check if the injected `IEnumerable<T>` of behaviors is an `IList<T>`. If so, build the pipeline using a backward `for` loop starting from `list.Count - 1` down to `0`. This avoids enumerator allocation entirely while constructing the pipeline backwards.

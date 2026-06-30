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

## 2026-06-16 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** In hot paths (like Mediator pipeline execution), using LINQ `.Reverse().Aggregate()` or `.Select().ToList()` creates hidden allocations (enumerators, delegates, intermediate objects) on every request.
**Action:** Always type-check `IEnumerable<T>` for `IPipelineBehavior[]` or `IList<T>` (or `ICollection<T>`) and use a backward `for` loop or `foreach` to construct delegate chains without LINQ allocations.

## 2026-06-18 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** In hot paths (like Mediator pipeline construction), using LINQ `.Reverse()` and `.Aggregate()` allocates enumerators and delegates which adds unnecessary per-request overhead. DI containers often return lists or arrays (`T[]`) when resolving `IEnumerable<T>`.
**Action:** When iterating over a collection returned by `IEnumerable<T>` from DI, type-check or cast to `IList<T>`, and use a backward `for` loop to build delegates. This enables zero-allocation enumeration and pipeline construction without LINQ overhead.

## 2026-06-20 - Avoid LINQ .Reverse().Aggregate(...) on collections for pipeline building
**Learning:** In hot paths (like pipeline creation for Mediator handlers), using LINQ `.Reverse().Aggregate(...)` allocates enumerators and delegates.
**Action:** When a collection is an `IList<T>` (which is very common for standard Microsoft DI returning arrays), bypass LINQ and use a backward `for` loop to build delegate chains to prevent allocations and save time.

## 2026-06-21 - Avoid LINQ .Select().ToList() on collections for performance
**Learning:** In hot paths (like notification publishing), using LINQ `.Select(...).ToList()` allocates enumerators and delegates, and often sizes the list iteratively.
**Action:** When a pre-sized list is needed from an existing collection, type-check for `ICollection<T>` or `IReadOnlyCollection<T>`, allocate a `List<T>` with that exact capacity, and manually populate it using a `foreach` loop to avoid LINQ allocation overhead.

## 2026-06-21 - Eliminate LINQ allocations in pipeline wrappers
**Learning:** In hot paths (like request and stream handling), using `.Reverse().Aggregate()` to build the pipeline creates unnecessary delegate allocations and reverses array/lists iteratively.
**Action:** When composing pipelines from Microsoft.Extensions.DependencyInjection, index backwards directly with a `for` loop (type-checking for `IList<T>`) to achieve zero-allocation pipeline construction.

## 2026-06-21 - Avoid LINQ .Reverse().Aggregate() for pipeline construction
**Learning:** Using LINQ `.Reverse().Aggregate(...)` to build Russian-doll Request/Stream pipelines allocates enumerators and delegates, impacting per-request heap allocations in hot paths.
**Action:** When iterating over a collection from MS.DI (`IEnumerable<T>`), type-check for `IList<T>` (since MS.DI often returns arrays) and use a backward `for` loop, safely capturing variables in a scoped iteration instead of allocating LINQ operators.

## 2026-06-23 - Avoid LINQ .Reverse() and .Aggregate() in Delegate Pipeline Construction
**Learning:** Using LINQ `.Reverse()` and `.Aggregate()` to build delegate pipelines in hot paths (like request dispatchers) incurs significant per-request allocations. It allocates an enumerator for `.Reverse()`, an enumerator for `.Aggregate()`, and delegates for the aggregation function. This can noticeably drop performance and increase GC pressure when multiple pipeline behaviors are registered.
**Action:** Always prefer looping backward via indexers to construct the pipeline when building nested delegates. If using a collection returned by DI, check if it's an `IList<T>` (as they often return lists or arrays) to allow index-based access, and only fallback to `.ToArray()` if it isn't.

## 2024-06-30 - Avoid IEnumerator allocation in notification publishers
**Learning:** In C#, using `foreach` over `IEnumerable<T>` creates an `IEnumerator` allocation on the heap since it boxes the struct enumerators via the interface. On the publisher hot path, avoiding this allocation reduces GC overhead.
**Action:** When a collection is likely an Array or `List<T>` (e.g. injected by DI), use a type-check for `IList<T>` or `IReadOnlyList<T>` and iterate it backwards or forwards using a `for` loop to avoid IEnumerator allocation entirely. Use `foreach` only as a fallback.

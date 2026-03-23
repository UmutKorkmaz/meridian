namespace Meridian.Mapping.Tests;

// ── Basic Models ────────────────────────────────────────────────

public class Source
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class Destination
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
}

// ── Nested Models ───────────────────────────────────────────────

public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
}

public class PersonSource
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

public class PersonDest
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

// ── Flattening Models ───────────────────────────────────────────

public class FlatPersonDest
{
    public string Name { get; set; } = string.Empty;
    public string AddressStreet { get; set; } = string.Empty;
    public string AddressCity { get; set; } = string.Empty;
}

// ── Multi-level Flattening Models ───────────────────────────────

public class Country
{
    public string Name { get; set; } = string.Empty;
}

public class Region
{
    public Country Country { get; set; } = new();
}

public class Company
{
    public Region Region { get; set; } = new();
}

public class FlatCompanyDest
{
    public string RegionCountryName { get; set; } = string.Empty;
}

// ── Different Property Names ────────────────────────────────────

public class EmployeeSource
{
    public string FullName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int YearsExperience { get; set; }
}

public class EmployeeDest
{
    public string Name { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public int Experience { get; set; }
}

// ── Collection Models ───────────────────────────────────────────

public class OrderSource
{
    public int OrderId { get; set; }
    public List<OrderItemSource> Items { get; set; } = new();
}

public class OrderItemSource
{
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public class OrderDest
{
    public int OrderId { get; set; }
    public List<OrderItemDest> Items { get; set; } = new();
}

public class OrderItemDest
{
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

// ── ICollection Models ──────────────────────────────────────────

public class CollectionHolder<T>
{
    public ICollection<T> Items { get; set; } = new List<T>();
}

// ── Enum Models ─────────────────────────────────────────────────

public enum SourceStatus { Active, Inactive, Pending }
public enum DestStatus { Active, Inactive, Pending }
public enum DifferentEnumValues { Running = 0, Stopped = 1, Waiting = 2 }

public class StatusSource
{
    public SourceStatus Status { get; set; }
}

public class StatusDest
{
    public DestStatus Status { get; set; }
}

// ── Nullable Models ─────────────────────────────────────────────

public class NullableSource
{
    public int? Value { get; set; }
    public string? Text { get; set; }
    public DateTime? Date { get; set; }
}

public class NullableDest
{
    public int? Value { get; set; }
    public string? Text { get; set; }
    public DateTime? Date { get; set; }
}

public class NonNullableDest
{
    public int Value { get; set; }
}

public class NonNullableSource
{
    public int Value { get; set; }
}

public class NullableIntDest
{
    public int? Value { get; set; }
}

// ── Constructor Mapping Models ──────────────────────────────────

public class ImmutableDest
{
    public string Name { get; }
    public int Age { get; }

    public ImmutableDest(string name, int age)
    {
        Name = name;
        Age = age;
    }
}

public class MultiCtorDest
{
    public string Name { get; }
    public int Value { get; }

    public MultiCtorDest(string name) : this(name, 0) { }

    public MultiCtorDest(string name, int value)
    {
        Name = name;
        Value = value;
    }
}

// ── Circular Reference Models ───────────────────────────────────

public class TreeNode
{
    public string Value { get; set; } = string.Empty;
    public TreeNode? Parent { get; set; }
    public List<TreeNode> Children { get; set; } = new();
}

public class TreeNodeDto
{
    public string Value { get; set; } = string.Empty;
    public TreeNodeDto? Parent { get; set; }
    public List<TreeNodeDto> Children { get; set; } = new();
}

// ── Inheritance Models ──────────────────────────────────────────

public class AnimalSource
{
    public string Name { get; set; } = string.Empty;
    public int Legs { get; set; }
}

public class DogSource : AnimalSource
{
    public string Breed { get; set; } = string.Empty;
}

public class AnimalDest
{
    public string Name { get; set; } = string.Empty;
    public int Legs { get; set; }
}

public class DogDest : AnimalDest
{
    public string Breed { get; set; } = string.Empty;
}

public class CatSource : AnimalSource
{
    public bool Declawed { get; set; }
}

public class CatDest : AnimalDest
{
    public bool Declawed { get; set; }
}

// ── ForPath / IncludeMembers Models ──────────────────────────────

public class AddressSource
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public class UserSource
{
    public string Name { get; set; } = string.Empty;
    public AddressSource? Address { get; set; }
}

public class AddressDestNested
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public class UserDestNested
{
    public string Name { get; set; } = string.Empty;
    public AddressDestNested Address { get; set; } = new();
}

public class PersonDetails
{
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class PersonWithDetailsSource
{
    public string Name { get; set; } = string.Empty;
    public PersonDetails? Details { get; set; }
}

public class PersonDetailsDest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Age { get; set; }
}

// ── ConvertUsing Member Models ───────────────────────────────────

public class BirthDateSource
{
    public DateTime BirthDate { get; set; }
}

public class BirthDateDest
{
    public int Age { get; set; }
}

public class DateToAgeConverter : Meridian.Mapping.Converters.IValueConverter<DateTime, int>
{
    public int Convert(DateTime sourceMember, Meridian.Mapping.Execution.ResolutionContext context)
    {
        var today = DateTime.Today;
        var age = today.Year - sourceMember.Year;
        if (sourceMember > today.AddYears(-age)) age--;
        return age;
    }
}

// ── Value Resolver Test Models ──────────────────────────────────

public class ResolverSource
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public class ResolverDest
{
    public string FullName { get; set; } = string.Empty;
}

// ── Profile Test Models ─────────────────────────────────────────

public class ProductSource
{
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
}

public class ProductDest
{
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
}

// ── All Ignored Dest ────────────────────────────────────────────

public class AllIgnoredDest
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

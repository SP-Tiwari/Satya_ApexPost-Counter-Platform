using ApexPost.Domain.Enums;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Rows of the lookup tables behind the StaffRole and ProductCategory enums.
// The domain keeps its enums; these exist only so the database can enforce the
// allowed values with a foreign key.
public sealed record RoleRecord(StaffRole Id, string Name);

public sealed record ProductCategoryRecord(ProductCategory Id, string Name);

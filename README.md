
[![GitHub Actions](https://github.com/simplify9/EfCoreExtensions/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/simplify9/EfCoreExtensions/actions/workflows/nuget-publish.yml)
[![NuGet](https://img.shields.io/nuget/v/SimplyWorks.EfCoreExtensions?style=for-the-badge)](https://www.nuget.org/packages/SimplyWorks.EfCoreExtensions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge)](LICENSE)

| **Package**       | **Version** |
| :----------------:|:----------------------:|
|`SimplyWorks.EfCoreExtensions`| [![NuGet](https://img.shields.io/nuget/v/SimplyWorks.EfCoreExtensions?style=for-the-badge)](https://www.nuget.org/packages/SimplyWorks.EfCoreExtensions) |


## Overview

This repository contains open-source Entity Framework Core extensions for .NET, provided by Simplify9. The main NuGet package is [`SimplyWorks.EfCoreExtensions`](https://www.nuget.org/packages/SimplyWorks.EfCoreExtensions), targeting `net8.0` and licensed under MIT.

### Main Features
The extensions are organized by file and provide additional helpers for EF Core, including:

- **ChangeTrackerExtensions.cs**: Soft deletion, audit values, tenant values, domain event dispatching.
- **DbContextExtensions.cs**: Helpers for working with DbContext and relational types.
- **EntityEntryExtensions.cs**: Property setting utilities.
- **EntityTypeBuilderExtensions.cs**: Entity type builder helpers.
- **ExpressionExtensions.cs**: Expression utilities.
- **ICollectionExtensions.cs**: Collection update helpers.
- **IHostExtensions.cs**: Host-related helpers.
- **IQueryableOfTExtensions.cs**: Queryable extensions and search conditions.
- **ModelBuilderExtensions.cs**: Model builder helpers.
- **OwnedNavigationBuilderExtensions.cs**: Owned navigation builder helpers.

See the source files for the full list of available extension methods and their usage.

### Projects
- `SW.EfCoreExtensions`: Main EF Core extensions library.
- `SW.EfCoreExtensions.PgSql`: PostgreSQL-specific extensions.
- `SW.EfCoreExtensions.UnitTests`: Unit tests for the extensions.

### License
This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

### Build & CI
GitHub Actions are used for CI/CD. See the badge above for status. NuGet packages are published automatically on new releases.

>`Contact> BuildContact<TOwner>`

>`Dimensions> BuildDimensions<TOwner>`

>`Weight> BuildWeight<TOwner>`

>`Money> BuildMoney<TOwner>`

>`Audit> BuildAudit<TOwner>`

#### PropertyBuilderExtensions.cs

#### RelationalDbType.cs

#### [Searchy](https://github.com/simplify9/Searchy)
>`Search<TEntity>`

>`SearchyCondition`

>`SearchyFilter`

>`SearchySort`


## Getting support 👷
If you encounter any bugs, don't hesitate to submit an [issue](https://github.com/simplify9/EfCoreExtensions/issues). We'll get back to you promptly!

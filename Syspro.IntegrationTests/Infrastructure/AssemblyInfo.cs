// Marks every test in this project, which allows filtering across the solution:
//   dotnet test --filter "Category!=Integration"
[assembly: AssemblyTrait("Category", "Integration")]

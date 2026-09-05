using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Content.Tests")]
[assembly: InternalsVisibleTo("Content.IntegrationTests")]
// FORK PATCH K4 (docs/upstream-patches.md): the Content.AiBench bench calls into server internals.
[assembly: InternalsVisibleTo("Content.AiBench")]

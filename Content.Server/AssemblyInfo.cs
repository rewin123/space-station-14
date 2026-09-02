using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Content.Tests")]
[assembly: InternalsVisibleTo("Content.IntegrationTests")]
// FORK PATCH К4 (docs/upstream-patches.md): стенд Content.AiBench зовёт внутренности сервера.
[assembly: InternalsVisibleTo("Content.AiBench")]

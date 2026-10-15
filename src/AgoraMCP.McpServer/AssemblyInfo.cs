using System.Runtime.CompilerServices;

// The CLI is internal on purpose (not part of the MCP surface), but it is also
// the test/debug path, so the test assembly gets a friend pass at it.
[assembly: InternalsVisibleTo("AgoraMCP.Tests")]

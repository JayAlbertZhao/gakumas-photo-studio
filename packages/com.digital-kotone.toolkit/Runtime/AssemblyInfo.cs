using System.Runtime.CompilerServices;

// Existing application diagnostics use internal implementation contracts.
// Public consumers do not need this privileged access.
[assembly: InternalsVisibleTo("Gakumas.PhotoStudio")]

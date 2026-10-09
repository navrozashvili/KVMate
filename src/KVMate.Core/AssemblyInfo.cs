using System.Runtime.CompilerServices;

// The interop declarations are internal because nothing outside Core may bind to them. The tests
// see them only so a fake can stand where a real wrapper would.
[assembly: InternalsVisibleTo("KVMate.Core.Tests")]

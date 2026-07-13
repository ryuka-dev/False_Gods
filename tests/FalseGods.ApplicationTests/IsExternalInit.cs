namespace System.Runtime.CompilerServices
{
    // The test assembly targets net472 too, so a positional `record` declared in these tests needs the same
    // compile-time IsExternalInit shim the production assemblies carry (Directory.Build.props sets net472).
    internal static class IsExternalInit
    {
    }
}

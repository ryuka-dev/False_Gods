namespace System.Runtime.CompilerServices
{
    // net472 (this repo's TargetFramework, Directory.Build.props) does not ship
    // System.Runtime.CompilerServices.IsExternalInit, which the C# compiler requires in order to emit the
    // init-only setters that positional `record` declarations generate. This internal shim supplies the
    // type as a pure compile-time contract; it carries no behaviour and is never referenced at runtime.
    // It is `internal`, so it cannot collide with the framework-supplied type on any target that already
    // defines one — the same shim FalseGods.Protocol carries for the same reason.
    internal static class IsExternalInit
    {
    }
}

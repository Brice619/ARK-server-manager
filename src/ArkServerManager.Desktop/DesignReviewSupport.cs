namespace ArkServerManager.Desktop;
internal static class DesignReviewSupport
{
#if DESIGN_REVIEW
    internal const bool Enabled=true;
#else
    internal const bool Enabled=false;
#endif
}

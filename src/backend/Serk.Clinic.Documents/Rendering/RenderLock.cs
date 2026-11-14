namespace Serk.Clinic.Documents.Rendering;

/// <summary>Single process-wide gate for PDF rendering (see the renderers' Render methods).</summary>
internal static class RenderLock
{
    public static readonly Lock Gate = new();
}

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Pulse.Web;

public static class RenderModes
{
    /// <summary>
    /// Interactive, with no first server-side render: the pages behind the sign-in load their data once,
    /// when the live connection starts, instead of twice.
    /// </summary>
    public static readonly IComponentRenderMode InteractiveNoPrerender = new InteractiveServerRenderMode(prerender: false);
}

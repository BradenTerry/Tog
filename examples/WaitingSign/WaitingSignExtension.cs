using Tog.Extensions;

namespace WaitingSign;

public sealed class WaitingSignExtension : ITogExtension
{
    // An overlay rather than a view: an agent can start waiting while you are
    // looking at another one, and a tab for that agent would not be on screen.
    public void Configure(IExtensionBuilder builder) => builder.AddOverlay<SignOverlay>("sign");
}

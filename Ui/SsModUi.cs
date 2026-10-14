using Robust.Client.UserInterface;
using Robust.Shared.IoC;

namespace SsMod.Ui;

public static class SsModUi
{
    public static void Create()
    {
        var ui = IoCManager.Resolve<IUserInterfaceManager>();
        //ui.RootControl.AddChild(new SsModPanel());
    }
}

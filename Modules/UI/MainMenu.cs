using Gtk;

namespace VRPC.Packaging
{
    static class VRPCManager
    {
        static public void Start()
        {
            Application.Init();

            Window window = new Window("VRPC Application");
            window.SetDefaultSize(640, 480);

            Button backButton = new Button { Label = "Back" };
            Button nextButton = new Button { Label = "Next" };
            Button cancelButton = new Button { Label = "Cancel" };

            Box buttonBox = new Box(Orientation.Horizontal, 10);
            buttonBox.Add(backButton);
            buttonBox.Add(nextButton);
            buttonBox.Add(cancelButton);

            Label dataLabel = new Label();
            dataLabel.Text = "Sample Data";

            Box mainBox = new Box(Orientation.Vertical, 10);
            mainBox.Add(dataLabel);
            mainBox.Add(buttonBox);

            window.Add(mainBox);

            window.ShowAll();

            Application.Run();
        }
    }
}
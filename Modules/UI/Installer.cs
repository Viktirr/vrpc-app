using Gtk;

namespace VRPC.Packaging
{
    public class VRPCInstaller : Window
    {
        public VRPCInstaller() : base("VRPC Installer")
        {
            SetDefaultSize(800, 400);
            Resizable = false;
            DeleteEvent += (o, args) => Application.Quit();

            // Main container
            var mainBox = new VBox();
            Add(mainBox);

            // Header
            var header = new Label("<span size='x-large' weight='bold'>VRPC Installer</span>");
            header.UseMarkup = true;
            mainBox.PackStart(header, false, false, 20);

            // Description
            var description = new Label("VPRC is an application that allows communication between the extension and client to provide Rich Presence data to Discord alongside other features.\n\nBy clicking Install below, you agree to the License, Terms of Use and Privacy Policy of this application listed over at https://viktir.com/vrpc\n\nIf you do not agree with any of the previous notices, refrain from installing this software by clicking the Cancel button or exiting the program.\n\nBy selecting install the following will occur:\nThe files for this application will extract at %appdata%\\VRPCApp\nA few registry keys will be created to allow for Native Messaging permissions to work with the extension and to create an entry for uninstalling the application in the Settings app.")
            {
                LineWrap = true,
                Justify = Justification.Center
            };
            mainBox.PackStart(description, false, false, 20);

            // Button container
            var buttonBox = new HBox(false, 10) // Set homogeneous to false
            {
                BorderWidth = 20
            };
            mainBox.PackEnd(buttonBox, false, true, 0); // Fill horizontally

            // Create buttons with style class
            var installButton = new Button("Install") { Name = "mainButtons" };
            var cancelButton = new Button("Cancel") { Name = "altButtons" };

            // Add a spacer that expands to push buttons to the right
            buttonBox.PackStart(new Label(), true, true, 0); // Expands horizontally

            // Add buttons to the HBox
            buttonBox.PackStart(installButton, false, false, 0);
            buttonBox.PackStart(cancelButton, false, false, 0);

            KeyPressEvent += (o, args) =>
            {
                if (args.Event.Key == Gdk.Key.r)
                {
                    Destroy();
                    new VRPCInstaller().ShowAll();
                }
            };

            string cssString;
            using (var cssFile = new System.IO.StreamReader("D:\\styles.css")) // Oops, temporary while I still figure things out
            {
                cssString = cssFile.ReadToEnd();
            }

            var cssProvider = new CssProvider();
            cssProvider.LoadFromData(cssString);
            StyleContext.AddProviderForScreen(
                Gdk.Screen.Default,
                cssProvider,
                StyleProviderPriority.Application
            );

            ShowAll();
        }
    }
}
namespace VRPC.Packaging
{
    public static class PackagingGlobals
    {
        public static string sameFolderString = "";
        public static string uninstallString = "";

        public static string cssString =
@"window {
    background-color: #222;
    color: #fff;
}
/* Remove default button decorations */
button#mainButtons {
    background-image: none;
    background-color: rgba(211, 211, 211, 0.3);
    color: #fff;
    border-radius: 24px;
    padding: 10px 30px;
    border: 1px solid rgba(211, 211, 211, 0.8);
    outline: none;
    box-shadow: none;
    min-width: 120px;
    -gtk-icon-shadow: none;
    text-shadow: none;
}

button#altButtons {
    background-image: none;
    background-color: rgba(211, 16, 16, 0.3);
    color: #fff;
    border-radius: 24px;
    padding: 10px 30px;
    border: 1px solid rgba(211, 16, 16, 0.8);
    outline: none;
    box-shadow: none;
    min-width: 120px;
    -gtk-icon-shadow: none;
    text-shadow: none;
}

/* Fix focus state */
button#mainButtons:focus button#altButtons:focus {
    box-shadow: none;
    outline: none;
}

button#mainButtons:hover {
    background-color: rgba(211, 211, 211, 0.8);
    color:#fff;
}

button#altButtons:hover {
    background-color: rgba(211, 16, 16, 0.8);
    color: #fff;
}

/* Remove inner shadow on active state */
button#mainButtons:active,
button#mainButtons:checked,
button#altButtons:active,
button#altButtons:checked {
    background-image: none;
    box-shadow: none;
}

/* Target the label inside buttons directly */
button#mainButtons label, button#altButtons label {
    color: #fff;
    text-shadow: none;
    padding: 0;
    margin: 0;
}

button#mainButtons:hover label, button#altButtons:hover label {
    color: #000;
    text-shadow: none;
    padding: 0;
    margin: 0;
}
/* Remove default GtkButton inner padding */
button.mainButtons > *, button.altButtons label {
    padding: 0;
    margin: 0;
    background-color: transparent;
}";

    }
}
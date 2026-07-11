using System;
using System.Collections.Generic;

namespace GameCult.Eve.Surface
{
    public sealed class EveInputDeviceLayout
    {
        public string LayoutId { get; set; } = "";
        public string DeviceClass { get; set; } = "";
        public IReadOnlyList<EveInputLayoutControl> Controls { get; set; } = Array.Empty<EveInputLayoutControl>();
        public bool SupportsDirectionalStrings { get; set; }

        public static EveInputDeviceLayout AnsiKeyboardMouse() => new EveInputDeviceLayout
        {
            LayoutId = "keyboard.ansi-104+mouse.standard",
            DeviceClass = "keyboard-mouse",
            Controls = new[]
            {
                Key("keyboard.escape", "Esc", 0, 0, 1.25f), Key("keyboard.f1", "F1", 2, 0), Key("keyboard.f2", "F2", 3, 0),
                Key("keyboard.1", "1", 1, 1), Key("keyboard.2", "2", 2, 1), Key("keyboard.3", "3", 3, 1), Key("keyboard.4", "4", 4, 1), Key("keyboard.5", "5", 5, 1),
                Key("keyboard.tab", "Tab", 0, 2, 1.5f), Key("keyboard.q", "Q", 1.5f, 2), Key("keyboard.w", "W", 2.5f, 2), Key("keyboard.e", "E", 3.5f, 2), Key("keyboard.r", "R", 4.5f, 2),
                Key("keyboard.a", "A", 1.75f, 3), Key("keyboard.s", "S", 2.75f, 3), Key("keyboard.d", "D", 3.75f, 3), Key("keyboard.f", "F", 4.75f, 3),
                Key("keyboard.leftShift", "Shift", 0, 4, 2.25f), Key("keyboard.space", "Space", 3.75f, 5, 6.25f),
                Key("mouse.primary", "Mouse 1", 18, 1), Key("mouse.secondary", "Mouse 2", 19, 1), Key("mouse.middle", "Mouse 3", 18.5f, 2)
            }
        };

        public static EveInputDeviceLayout StandardGamepad() => new EveInputDeviceLayout
        {
            LayoutId = "gamepad.standard",
            DeviceClass = "gamepad",
            SupportsDirectionalStrings = true,
            Controls = new[]
            {
                Key("gamepad.leftStick", "Left Stick", 2, 3, 2), Key("gamepad.rightStick", "Right Stick", 8, 4, 2),
                Key("gamepad.dpad.up", "Up", 1, 1), Key("gamepad.dpad.left", "Left", 0, 2), Key("gamepad.dpad.right", "Right", 2, 2), Key("gamepad.dpad.down", "Down", 1, 3),
                Key("gamepad.buttonSouth", "A / Cross", 10, 3), Key("gamepad.buttonEast", "B / Circle", 11, 2), Key("gamepad.buttonWest", "X / Square", 9, 2), Key("gamepad.buttonNorth", "Y / Triangle", 10, 1),
                Key("gamepad.leftTrigger", "LT / L2", 2, 0, 2), Key("gamepad.rightTrigger", "RT / R2", 9, 0, 2)
            }
        };

        private static EveInputLayoutControl Key(string id, string label, float x, float y, float width = 1) =>
            new EveInputLayoutControl { Control = id, Label = label, X = x, Y = y, Width = width, Height = 1 };
    }

    public sealed class EveInputLayoutControl
    {
        public string Control { get; set; } = "";
        public string Label { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; } = 1;
        public float Height { get; set; } = 1;
    }
}

using System;
using System.Linq;
using NesXbox.Logic;
using Windows.Gaming.Input;

namespace NesXbox.Services
{
    public struct PadState
    {
        public NesButtons Player1;
        public NesButtons Player2;
        public bool PausePressed;   // View+Menu chord, edge-triggered
        public int Connected;
    }

    /// <summary>
    /// Xbox controller -> NES pad mapping. Safe to poll from the emulation thread.
    /// A=A, B/X=B, Menu=Start, View=Select, D-pad or left stick = directions; View+Menu opens the pause menu.
    /// </summary>
    public sealed class GamepadService
    {
        const double StickThreshold = 0.5;
        bool pauseChordHeld;

        public event Action ConnectionChanged;

        public GamepadService()
        {
            Gamepad.GamepadAdded += (s, g) => ConnectionChanged?.Invoke();
            Gamepad.GamepadRemoved += (s, g) => ConnectionChanged?.Invoke();
        }

        public int ConnectedCount => Gamepad.Gamepads.Count;

        public PadState Poll()
        {
            var pads = Gamepad.Gamepads.ToList(); // snapshot: the list changes on hot-plug
            var state = new PadState { Connected = pads.Count };
            if (pads.Count > 0)
            {
                var r = pads[0].GetCurrentReading();
                bool chord = (r.Buttons & GamepadButtons.View) != 0 && (r.Buttons & GamepadButtons.Menu) != 0;
                state.PausePressed = chord && !pauseChordHeld;
                pauseChordHeld = chord;
                state.Player1 = Map(r);
                if (chord) state.Player1 &= ~(NesButtons.Start | NesButtons.Select);
            }
            else pauseChordHeld = false;
            if (pads.Count > 1) state.Player2 = Map(pads[1].GetCurrentReading());
            return state;
        }

        public static NesButtons Map(GamepadReading r)
        {
            var b = r.Buttons;
            NesButtons n = NesButtons.None;
            if ((b & GamepadButtons.A) != 0) n |= NesButtons.A;
            if ((b & (GamepadButtons.B | GamepadButtons.X)) != 0) n |= NesButtons.B;
            if ((b & GamepadButtons.Menu) != 0) n |= NesButtons.Start;
            if ((b & GamepadButtons.View) != 0) n |= NesButtons.Select;
            if ((b & GamepadButtons.DPadUp) != 0 || r.LeftThumbstickY > StickThreshold) n |= NesButtons.Up;
            if ((b & GamepadButtons.DPadDown) != 0 || r.LeftThumbstickY < -StickThreshold) n |= NesButtons.Down;
            if ((b & GamepadButtons.DPadLeft) != 0 || r.LeftThumbstickX < -StickThreshold) n |= NesButtons.Left;
            if ((b & GamepadButtons.DPadRight) != 0 || r.LeftThumbstickX > StickThreshold) n |= NesButtons.Right;
            // The NES pad cannot press opposite directions; some games crash if it does.
            if ((n & (NesButtons.Up | NesButtons.Down)) == (NesButtons.Up | NesButtons.Down)) n &= ~(NesButtons.Up | NesButtons.Down);
            if ((n & (NesButtons.Left | NesButtons.Right)) == (NesButtons.Left | NesButtons.Right)) n &= ~(NesButtons.Left | NesButtons.Right);
            return n;
        }
    }
}

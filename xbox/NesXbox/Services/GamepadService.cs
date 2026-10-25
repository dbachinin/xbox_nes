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
        public bool PausePressed;      // RT pressed this frame (edge-triggered)
        public bool StateMenuPressed;  // LT pressed this frame (edge-triggered)
        public int Connected;
    }

    /// <summary>
    /// Xbox controller -> NES pad mapping. Safe to poll from the emulation thread.
    /// NES layout (B left of A): Xbox A=NES B, Xbox B=NES A, X=turbo B, Y=turbo A,
    /// Menu=Start, View=Select, D-pad or left stick = directions.
    /// RT opens the pause menu, LT the save-state menu; the Xbox and Share buttons stay with the system.
    /// </summary>
    public sealed class GamepadService
    {
        const double StickThreshold = 0.5;
        const int TurboHalfPeriod = 2;   // frames on / frames off: 15 presses per second at 60 Hz
        const double TriggerPress = 0.6, TriggerRelease = 0.3; // hysteresis: an analog trigger must not chatter
        bool rightHeld, leftHeld;
        int pollCount;

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
            bool turboOn = (pollCount++ / TurboHalfPeriod) % 2 == 0; // Poll runs once per emulated frame
            if (pads.Count > 0)
            {
                var r = pads[0].GetCurrentReading();
                state.PausePressed = Edge(r.RightTrigger, ref rightHeld);
                state.StateMenuPressed = Edge(r.LeftTrigger, ref leftHeld);
                state.Player1 = Map(r, turboOn);
            }
            else rightHeld = leftHeld = false;
            if (pads.Count > 1) state.Player2 = Map(pads[1].GetCurrentReading(), turboOn);
            return state;
        }

        static bool Edge(double value, ref bool held)
        {
            bool pressed = held ? value > TriggerRelease : value > TriggerPress;
            bool edge = pressed && !held;
            held = pressed;
            return edge;
        }

        public static NesButtons Map(GamepadReading r, bool turboOn)
        {
            var b = r.Buttons;
            NesButtons n = NesButtons.None;
            if ((b & GamepadButtons.B) != 0 || ((b & GamepadButtons.Y) != 0 && turboOn)) n |= NesButtons.A;
            if ((b & GamepadButtons.A) != 0 || ((b & GamepadButtons.X) != 0 && turboOn)) n |= NesButtons.B;
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SenseAPI;

namespace Sensetest
{
    class Program
    {
        static void Main(string[] args) {
            Senseboard sense = new Senseboard("COM5");

            Debug.Listeners.Add(new ConsoleTraceListener());

            /* Event based input handling. :) */

            // Button was pressed.
            sense.OnButtonChanged += (sender, b) => {
                Console.WriteLine("Button is {0}", b);
            };

            // Infrared sensor
            sense.OnInfraredChanged += (sender, b) => {
                Console.WriteLine("Infra is {0}", b);
            };

            // Resistive inputs
            sense.OnResistiveInputChanged += (sender, tuple) => {
                Console.WriteLine("Resistive Input {0} is {1}", tuple.Input, tuple.Value);
            };

            // Slider 
            sense.OnSliderChanged += (sender, i) => {
                Console.WriteLine("Slider is {0}", i);
            };

            // Sound sensor (Disabled by default because of console spam)
            sense.OnSoundChanged += (sender, i) => {
                Console.WriteLine("Sound is {0}", i);
            };             

            /* Now a loltastic example of setting LED's and polling for input */

            // Turn all the LED's off
            sense.SetLED(LightMask.All, LightState.Off);

            // Move the slider to light up LED's
            LightMask mask = LightMask.None;
            while (true) {

                if (sense.Slider > 12)
                    mask |= LightMask.One;
                if (sense.Slider > 36)
                    mask |= LightMask.Two;
                if (sense.Slider > 48)
                    mask |= LightMask.Three;
                if (sense.Slider > 60)
                    mask |= LightMask.Four;
                if (sense.Slider > 72)
                    mask |= LightMask.Five;
                if (sense.Slider > 84)
                    mask |= LightMask.Six;
                if (sense.Slider > 96)
                    mask |= LightMask.Seven;

                sense.SetLED(LightMask.All, LightState.Off);
                sense.SetLED(mask, LightState.On);
                mask = LightMask.None;
            }
        }
    }
}

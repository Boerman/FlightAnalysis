﻿using MathNet.Numerics.Interpolation;
using MathNet.Numerics.LinearAlgebra.Double;
using Skyhop.FlightAnalysis.Internal;
using Skyhop.FlightAnalysis.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skyhop.FlightAnalysis
{
    internal static partial class MachineStates
    {
        internal static void Departing(this FlightContext context)
        {
            /*
             * First check plausible scenarios. The easiest to track is an aerotow.
             * 
             * If not, wait until the launch is completed.
             */

            if (context.Flight.LaunchMethod == LaunchMethods.None)
            {
                var departure = context.Flight.PositionUpdates
                    .Where(q => q.Heading != 0 && !double.IsNaN(q.Heading))
                    .OrderBy(q => q.TimeStamp)
                    .Take(5)
                    .ToList();

                if (departure.Count > 4)
                {
                    context.Flight.DepartureHeading = Convert.ToInt16(departure.Average(q => q.Heading));
                    context.Flight.DepartureLocation = departure.First().Location;

                    if (context.Flight.DepartureHeading == 0) context.Flight.DepartureHeading = 360;

                    // Only start method recognition after the heading has been determined
                    context.Flight.LaunchMethod = LaunchMethods.Unknown | LaunchMethods.Aerotow | LaunchMethods.Winch | LaunchMethods.Self;
                }
                else return;
            }

            if (context.Flight.DepartureTime != null &&
                (context.CurrentPosition.TimeStamp - (context.Flight.PositionUpdates.FirstOrDefault(q => q.Speed > 30)?.TimeStamp ?? context.CurrentPosition.TimeStamp)).TotalSeconds < 10) return;

            // We can safely try to extract the correct path

            if (context.Flight.LaunchMethod.HasFlag(LaunchMethods.Unknown | LaunchMethods.Aerotow))
            {
                var encounter = context
                    .IsAerotow()
                    .SingleOrDefault(q => q.Type == EncounterType.Tug || q.Type == EncounterType.Tow);

                if (encounter != null)
                {
                    context.Flight.LaunchMethod = LaunchMethods.Aerotow
                        | (encounter.Type == EncounterType.Tug
                            ? LaunchMethods.OnTow
                            : LaunchMethods.TowPlane);

                    context.Flight.Encounters.Add(encounter);

                    context.StateMachine.Fire(FlightContext.Trigger.TrackAerotow);

                    return;
                }

                context.Flight.LaunchMethod &= ~LaunchMethods.Aerotow;
            }

            // Hardwire a check to see if we're sinking again to abort the departure, but only if we're not behind a tow.
            if (!context.Flight.LaunchMethod.HasFlag(LaunchMethods.Aerotow)
                && context.Flight.PositionUpdates.Last().Altitude - context.CurrentPosition.Altitude > 3)
            {
                context.StateMachine.Fire(FlightContext.Trigger.Landing);
                return;
            }

            if (context.Flight.LaunchMethod.HasFlag(LaunchMethods.Unknown | LaunchMethods.Winch))
            {
                var x = new DenseVector(context.Flight.PositionUpdates.Select(w => (w.TimeStamp - context.Flight.DepartureTime.Value).TotalSeconds).ToArray());
                var y = new DenseVector(context.Flight.PositionUpdates.Select(w => w.Altitude).ToArray());

                var interpolation = CubicSpline.InterpolateNatural(x, y);

                var r = new List<double>();
                var r2 = new List<double>();

                for (var i = 0; i < (context.CurrentPosition.TimeStamp - context.Flight.DepartureTime.Value).TotalSeconds; i++)
                {
                    r.Add(interpolation.Differentiate(i));
                    r2.Add(interpolation.Differentiate2(i));
                }

                // When the initial climb has completed
                if (interpolation.Differentiate((context.CurrentPosition.TimeStamp - context.Flight.DepartureTime.Value).TotalSeconds) < 0)
                {
                    var averageHeading = context.Flight.PositionUpdates.Average(q => q.Heading);

                    // ToDo: Add check to see whether there is another aircraft nearby
                    if (context.Flight.PositionUpdates
                            .Skip(1)        // Skip the first element because heading is 0 when in rest
                            .Select(q => Geo.GetHeadingError(averageHeading, q.Heading))
                            .Any(q => q > 20)
                        || Geo.DistanceTo(
                            context.Flight.PositionUpdates.First().Location,
                            context.CurrentPosition.Location) > 3000)
                    {
                        context.Flight.LaunchMethod &= ~LaunchMethods.Winch;
                    }
                    else
                    {
                        context.Flight.LaunchFinished = context.CurrentPosition.TimeStamp;
                        context.Flight.LaunchMethod = LaunchMethods.Winch;
                        context.InvokeOnLaunchCompletedEvent();
                        context.StateMachine.Fire(FlightContext.Trigger.LaunchCompleted);
                    }
                }
            }
            else if (context.Flight.LaunchMethod.HasFlag(LaunchMethods.Unknown | LaunchMethods.Self))
            {
                context.Flight.LaunchFinished = context.CurrentPosition.TimeStamp;
                context.Flight.LaunchMethod = LaunchMethods.Self;
                context.InvokeOnLaunchCompletedEvent();
                context.StateMachine.Fire(FlightContext.Trigger.LaunchCompleted);
            }
        }
    }
}

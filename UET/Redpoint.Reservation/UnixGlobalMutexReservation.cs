﻿namespace Redpoint.Reservation
{
    using System.Runtime.Versioning;
    using System.Threading.Tasks;

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("linux")]
    internal class UnixGlobalMutexReservation : IGlobalMutexReservation
    {
        private readonly IReservation _reservation;

        public UnixGlobalMutexReservation(IReservation reservation)
        {
            _reservation = reservation;
        }

        public async ValueTask DisposeAsync()
        {
            await _reservation.DisposeAsync();
        }
    }
}
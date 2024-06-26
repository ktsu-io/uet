﻿namespace Redpoint.Uet.Core
{
    using System;
    using System.Numerics;
    using System.Security.Cryptography;
    using System.Text;

    internal sealed class DefaultStringUtilities : IStringUtilities
    {
        public string GetStabilityHash(string inputString, int? length)
        {
            var inputBytes = SHA256.HashData(Encoding.ASCII.GetBytes(inputString));
            const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz.-_";
            var dividend = new BigInteger(inputBytes);
            var builder = new StringBuilder();
            while (dividend != 0)
            {
                dividend = BigInteger.DivRem(dividend, alphabet.Length, out BigInteger remainder);
                builder.Insert(0, alphabet[Math.Abs((int)remainder)]);
            }
            if (!length.HasValue)
            {
                return builder.ToString().Trim('.');
            }
            else
            {
                return builder.ToString()[..length.Value].Trim('.');
            }
        }
    }
}

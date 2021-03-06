﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
using System;
using System.Security.Claims;
using System.Text;

namespace NuGet.Services.Metadata.Catalog.Ownership
{
    public class OwnershipOwner
    {
        public string NameIdentifier { get; set; }
        public string Name { get; set; }
        public string GivenName { get; set; }
        public string Surname { get; set; }
        public string Email { get; set; }
        public string Iss { get; set; }

        public Uri GetUri(Uri baseAddress)
        {
            string fragment = string.Format("#owner/{0}", NameIdentifier);
            return new Uri(baseAddress, fragment);
        }

        public string GetKey()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(NameIdentifier);
            string base64 = Convert.ToBase64String(bytes);
            return base64;
        }

        public static OwnershipOwner Create(ClaimsPrincipal claimsPrinciple)
        {
            return new OwnershipOwner
            {
                NameIdentifier = Get(claimsPrinciple, ClaimTypes.NameIdentifier, true),
                Name = Get(claimsPrinciple, ClaimTypes.Name),
                GivenName = Get(claimsPrinciple, ClaimTypes.GivenName),
                Surname = Get(claimsPrinciple, ClaimTypes.Surname),
                Email = Get(claimsPrinciple, ClaimTypes.Email),
                Iss = Get(claimsPrinciple, "iss")
            };
        }

        static string Get(ClaimsPrincipal claimsPrinciple, string type, bool isRequired = false)
        {
            Claim subject = ClaimsPrincipal.Current.FindFirst(type);
            if (subject == null)
            {
                if (isRequired)
                {
                    throw new Exception(string.Format("required Claim {0} not found", type));
                }
                else
                {
                    return null;
                }
            }
            return subject.Value;
        }
    }
}

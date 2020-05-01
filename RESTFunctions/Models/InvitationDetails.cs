﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace RESTFunctions.Models
{
    public class InvitationDetails
    {
        public string inviteEmail { get; set; }
        public string postRedeemUrl { get; set; }
        public Dictionary<string, string> additionalClaims { get; set; }
    }
}

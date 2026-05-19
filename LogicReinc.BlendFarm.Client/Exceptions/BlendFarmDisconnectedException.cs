using System;
using System.Collections.Generic;
using System.Text;

namespace LogicReinc.BlendFarm.Client.Exceptions
{
    public class BlendFarmDisconnectedException : Exception
    {
        public bool IsError { get; set; }
        public string Reason { get; set; }


        public BlendFarmDisconnectedException() : base("Render node disconnected") { }
        public BlendFarmDisconnectedException(string msg) : base(msg) { }
    }
}

// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MigAz.AWS
{
    public class Ec2Instance 
    {
        private string _instanceId, _instanceName;
        public Ec2Instance(string instanceId)
        {
            _instanceId = instanceId;
        }
        public Ec2Instance( string instanceId, string instanceName)
        {
            _instanceName = instanceName;
            _instanceId = instanceId;
        }
        public string InstanceName {
            get
            {
                return _instanceName;
            }
            private set
            {
                _instanceName = value;
            }
        }
        public string InstanceId {
            get
            {
                return _instanceId;
            }
            private set
            {
                _instanceId = value;
            }
        }
    }
}


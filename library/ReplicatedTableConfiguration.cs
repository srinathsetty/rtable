﻿// azure-rtable ver. 0.9
//
// Copyright (c) Microsoft Corporation
//
// All rights reserved.
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files
// (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify,
// merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished
// to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

namespace Microsoft.Azure.Toolkit.Replication
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Text.RegularExpressions;

    [DataContract(Namespace = "http://schemas.microsoft.com/windowsazure")]
    abstract public class ReplicatedTableConfigurationBase
    {
        abstract public Guid GetConfigId();
    }

    [DataContract(Namespace = "http://schemas.microsoft.com/windowsazure")]
    public class ReplicatedTableConfiguration : ReplicatedTableConfigurationBase
    {
        [DataMember(IsRequired = true, Order = 0)]
        internal protected Dictionary<string, ReplicatedTableConfigurationStore> viewMap = new Dictionary<string, ReplicatedTableConfigurationStore>();

        [DataMember(IsRequired = true, Order = 1)]
        internal protected List<ReplicatedTableConfiguredTable> tableList = new List<ReplicatedTableConfiguredTable>();

        [DataMember(IsRequired = true, Order = 2)]
        public int LeaseDuration { get; set; }

        [DataMember(IsRequired = true, Order = 3)]
        internal protected Guid Id { get; set; }

        public ReplicatedTableConfiguration()
        {
            Id = Guid.NewGuid();
            LeaseDuration = Constants.LeaseDurationInSec;
        }

        override public Guid GetConfigId()
        {
            return Id;
        }

        /*
         * View APIs:
         */
        public void SetView(string viewName, ReplicatedTableConfigurationStore config)
        {
            if (string.IsNullOrEmpty(viewName))
            {
                throw new ArgumentNullException("viewName");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            ThrowIfViewIsNotValid(viewName, config);

            // In case this is an update to an existing view,
            // the view should not break any existing constraint.
            ThrowIfViewBreaksTableConstraint(viewName, config);

            viewMap.Remove(viewName);
            viewMap.Add(viewName, config);
        }

        public ReplicatedTableConfigurationStore GetView(string viewName)
        {
            if (string.IsNullOrEmpty(viewName))
            {
                return null;
            }

            return !viewMap.ContainsKey(viewName) ? null : viewMap[viewName];
        }

        public void RemoveView(string viewName)
        {
            if (GetView(viewName) == null)
            {
                return;
            }

            ReplicatedTableConfiguredTable table = tableList.Find(e => viewName.Equals(e.ViewName, StringComparison.OrdinalIgnoreCase));
            if (table != null)
            {
                var msg = string.Format("View:\'{0}\' is referenced by table:\'{1}\'! First, delete the table then the view.",
                                        viewName,
                                        table.TableName);
                throw new Exception(msg);
            }

            viewMap.Remove(viewName);
        }

        private void ThrowIfViewIsNotValid(string viewName, ReplicatedTableConfigurationStore config)
        {
            if (config.ReplicaChain == null || config.ReplicaChain.Any(replica => replica == null))
            {
                var msg = string.Format("View:\'{0}\' has a null replica(s) !!!", viewName);
                throw new Exception(msg);
            }

            List<ReplicaInfo> chainList = config.GetCurrentReplicaChain();
            if (chainList.Any())
            {
                /* RULE 1:
                 * =======
                 * Read replicas rule:
                 *  - [R] replicas are contiguous from Tail backwards
                 *  - [R] replica count >= 1
                 */
                string readPattern = "^W*R+$";

                /* RULE 2:
                 * =======
                 * Write replicas rule:
                 *  - [W] replicas are contiguous from Head onwards
                 *  - [W] replica count = 0 or = ChainLength
                 */
                string writePattern = "^((R+)|(W+))$";

                // Get replica sequences
                string readSeq = "";
                string writeSeq = "";

                foreach (var replica in chainList)
                {
                    // Read sequence:
                    if (replica.IsReadable())
                    {
                        readSeq += "R";
                    }
                    else
                    {
                        readSeq += "W";
                    }

                    // Write sequence:
                    if (replica.IsWritable())
                    {
                        writeSeq += "W";
                    }
                    else
                    {
                        writeSeq += "R";
                    }
                }

                // Verify RULE 1:
                if (!Regex.IsMatch(readSeq, readPattern))
                {
                    var msg = string.Format("View:\'{0}\' has invalid Read chain:\'{1}\' !!!", viewName, readSeq);
                    throw new Exception(msg);
                }

                // Verify RULE 2:
                if (!Regex.IsMatch(writeSeq, writePattern))
                {
                    var msg = string.Format("View:\'{0}\' has invalid Write chain:\'{1}\' !!!", viewName, writeSeq);
                    throw new Exception(msg);
                }
            }
        }

        private void ThrowIfViewBreaksTableConstraint(string viewName, ReplicatedTableConfigurationStore config)
        {
            foreach (ReplicatedTableConfiguredTable table in tableList)
            {
                if (table.ConvertToRTable == false ||
                    string.IsNullOrEmpty(table.ViewName) ||
                    !table.ViewName.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Convertion mode: view shoud not have more than 1 replica
                List<ReplicaInfo> chainList = config.GetCurrentReplicaChain();
                if (chainList.Count <= 1)
                {
                    continue;
                }

                var msg = string.Format("Table:\'{0}\' should not have a view:\'{1}\' with more than 1 replica since it is in Convertion mode!",
                                        table.TableName,
                                        viewName);
                throw new Exception(msg);
            }
        }

        /*
         * Configured tables APIs:
         */
        public void SetTable(ReplicatedTableConfiguredTable config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            var tableName = config.TableName;
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentNullException("TableName");
            }

            // 1 - If pointing a view, then the view must exist ?
            ThrowIfViewIsMissing(config);

            // 2 - If table is in ConvertToRTable mode then view should have no more than 1 replica
            ThrowIfViewHasManyReplicasInConvertionMode(config);

            if (config.UseAsDefault == true)
            {
                // Allow only *one* default config => override previous, if any
                ReplicatedTableConfiguredTable found = tableList.Find(e => e.UseAsDefault == true);
                if (found != null)
                {
                    found.UseAsDefault = false;
                }
            }

            tableList.RemoveAll(e => tableName.Equals(e.TableName, StringComparison.OrdinalIgnoreCase));
            tableList.Add(config);
        }

        public ReplicatedTableConfiguredTable GetTable(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                return null;
            }

            return tableList.Find(e => tableName.Equals(e.TableName, StringComparison.OrdinalIgnoreCase));
        }

        public ReplicatedTableConfiguredTable GetDefaultConfiguredTable()
        {
            return tableList.Find(e => e.UseAsDefault == true);
        }

        public bool IsConfiguredTable(string tableName, out ReplicatedTableConfiguredTable configuredTable)
        {
            configuredTable = null;

            ReplicatedTableConfiguredTable config = GetTable(tableName) ?? GetDefaultConfiguredTable();

            // Neither explicit config, nor default config
            if (config == null)
            {
                return false;
            }

            // Placeholder config i.e. a config with No View
            if (string.IsNullOrEmpty(config.ViewName))
            {
                return false;
            }

            configuredTable = config;
            return true;
        }

        public void RemoveTable(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                return;
            }

            tableList.RemoveAll(e => tableName.Equals(e.TableName, StringComparison.OrdinalIgnoreCase));
        }

        private void ThrowIfViewIsMissing(ReplicatedTableConfiguredTable config)
        {
            if (string.IsNullOrEmpty(config.ViewName))
            {
                return;
            }

            if (GetView(config.ViewName) != null)
            {
                return;
            }

            var msg = string.Format("Table:\'{0}\' refers a missing view:\'{1}\'! First, create the view and then configure the table.",
                                    config.TableName,
                                    config.ViewName);
            throw new Exception(msg);
        }

        private void ThrowIfViewHasManyReplicasInConvertionMode(ReplicatedTableConfiguredTable config)
        {
            if (config.ConvertToRTable == false || string.IsNullOrEmpty(config.ViewName))
            {
                return;
            }

            ReplicatedTableConfigurationStore viewConfig = GetView(config.ViewName);
            // Assert (viewConfig != null)

            // In Convertion mode, view should not have more than 1 replica
            List<ReplicaInfo> chainList = viewConfig.GetCurrentReplicaChain();
            if (chainList.Count <= 1)
            {
                return;
            }

            var msg = string.Format("Table:\'{0}\' refers a view:\'{1}\' with more than 1 replica while in Convertion mode!",
                                    config.TableName,
                                    config.ViewName);
            throw new Exception(msg);
        }

        /*
         * Helpers ...
         */
        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            ReplicatedTableConfiguration other = obj as ReplicatedTableConfiguration;
            if (other == null)
            {
                return false;
            }

            return Id == other.Id;
        }

        internal protected void ValidateAndFixConfig()
        {
            /*
             * 1 - Views validation
             */
            // - Enforce viewMap not null
            if (viewMap == null)
            {
                viewMap = new Dictionary<string, ReplicatedTableConfigurationStore>();
            }
            else
            {
                //- Enforce viewName not empty
                viewMap.Remove("");

                // - Enforce config not null
                foreach (var key in viewMap.Keys.ToList().Where(key => viewMap[key] == null))
                {
                    viewMap.Remove(key);
                }

                // - Enforce replicas are not null, and well sequenced
                foreach (var entry in viewMap)
                {
                    var viewName = entry.Key;
                    var viewConf = entry.Value;

                    ThrowIfViewIsNotValid(viewName, viewConf);
                }
            }


            /*
             * 2 - Tables config validation
             */
            // - Enforce tableList not null
            if (tableList == null)
            {
                tableList = new List<ReplicatedTableConfiguredTable>();
            }
            else
            {
                //- Enforce table config not null
                tableList.RemoveAll(cfg => cfg == null);

                //- Enforce tableName not null per configured table
                tableList.RemoveAll(cfg => string.IsNullOrEmpty(cfg.TableName));

                // - Enforce no duplicate table config
                var duplicates = tableList.GroupBy(cfg => cfg.TableName).Where(group => group.Count() > 1).ToList();
                if (duplicates.Any())
                {
                    var msg = string.Format("Table:\'{0}\' is configured more than once! Only one config per table.", duplicates.First().Key);
                    throw new Exception(msg);
                }

                // Enforce that:
                tableList.TrueForAll(cfg =>
                {
                    // 1 - each table refers an existing view
                    ThrowIfViewIsMissing(cfg);

                    // 2 - and, each table in ConvertToRTable mode has no more than one replica
                    ThrowIfViewHasManyReplicasInConvertionMode(cfg);
                    return true;
                });

                // - Enforce no more than 1 default configured table (rule)
                if (tableList.Count(cfg => cfg.UseAsDefault) > 1)
                {
                    string msg = "Can't have more than 1 configured table as a default!";
                    throw new Exception(msg);
                }
            }
        }

        internal protected void MoveReplicaToHeadAndSetViewToReadOnly(string storageAccountName)
        {
            // Assert (storageAccountName != null)

            foreach (var entry in viewMap)
            {
                var viewName = entry.Key;
                var viewConf = entry.Value;

                viewConf.MoveReplicaToHeadAndSetViewToReadOnly(viewName, storageAccountName);
            }
        }

        internal protected void EnableWriteOnReplicas(string storageAccountName)
        {
            // Assert (storageAccountName != null)

            foreach (var entry in viewMap)
            {
                var viewName = entry.Key;
                var viewConf = entry.Value;

                viewConf.EnableWriteOnReplicas(viewName, storageAccountName);

                // Resulting view should not break any existing constraint.
                ThrowIfViewBreaksTableConstraint(viewName, viewConf);
            }
        }

        internal protected void EnableReadWriteOnReplicas(string storageAccountName, List<string> viewsToSkip)
        {
            // Assert (storageAccountName != null)
            // Assert viewsToSkip ! null

            foreach (var entry in viewMap)
            {
                var viewName = entry.Key;
                var viewConf = entry.Value;

                if (viewsToSkip.Any(v => v == viewName))
                {
                    // skip view
                    continue;
                }

                viewConf.EnableReadWriteOnReplica(viewName, storageAccountName);
            }
        }

        public override string ToString()
        {
            return ToJson();
        }

        public string ToJson()
        {
            return JsonStore<ReplicatedTableConfiguration>.Serialize(this);
        }

        public static ReplicatedTableConfiguration FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new ReplicatedTableConfiguration();
            }

            var config = JsonStore<ReplicatedTableConfiguration>.Deserialize(json);
            config.ValidateAndFixConfig();

            return config;
        }

        public static ReplicatedTableConfiguration MakeCopy(ReplicatedTableConfiguration config)
        {
            if (config == null)
            {
                return null;
            }

            var str = config.ToJson();
            return JsonStore<ReplicatedTableConfiguration>.Deserialize(str);
        }

        public static ReplicatedTableConfiguration GenerateNewConfigId(ReplicatedTableConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            var copy = MakeCopy(config);
            copy.Id = Guid.NewGuid();

            return copy;
        }
    }
}
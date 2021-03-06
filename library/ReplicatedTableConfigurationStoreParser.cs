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
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.WindowsAzure.Storage.Table;

    internal class ReplicatedTableConfigurationStoreParser : IReplicatedTableConfigurationParser
    {
        public const string DefaultViewName = "DefaultViewName";
        public const string AllTables = "AllTables";

        /// <summary>
        /// Parses the RTable configuration blobs.
        /// Returns the list of views, the list of configured tables and the lease duration.
        /// If null is returned, then the value of tableConfigList/leaseDuration are not relevant.
        /// </summary>
        /// <param name="blobs"></param>
        /// <param name="useHttps"></param>
        /// <param name="tableConfigList"></param>
        /// <param name="leaseDuration"></param>
        /// <returns></returns>
        public List<View> ParseBlob(
                                List<CloudBlockBlob> blobs,
                                Action<ReplicaInfo> SetConnectionString,
                                out List<ReplicatedTableConfiguredTable> tableConfigList,
                                out int leaseDuration,
                                out Guid configId)
        {
            tableConfigList = null;
            leaseDuration = 0;
            configId = Guid.Empty;

            ReplicatedTableConfigurationStore configurationStore;
            List<string> eTags;

            ReplicatedTableQuorumReadResult result = CloudBlobHelpers.TryReadBlobQuorum(
                                                                    blobs,
                                                                    out configurationStore,
                                                                    out eTags,
                                                                    JsonStore<ReplicatedTableConfigurationStore>.Deserialize);
            if (result.Code != ReplicatedTableQuorumReadCode.Success)
            {
                ReplicatedTableLogger.LogError("Unable to refresh view, \n{0}", result.ToString());
                return null;
            }


            /**
             * View:
             */
            var view = View.InitFromConfigVer1(DefaultViewName, configurationStore, SetConnectionString);
            view.RefreshTime = DateTime.UtcNow;

            if (view.ViewId <= 0)
            {
                ReplicatedTableLogger.LogError("ViewId={0} is invalid. Must be >= 1.", view.ViewId);
                return null;
            }

            if (view.IsEmpty)
            {
                ReplicatedTableLogger.LogError("ViewName={0} is empty, skipping ...", view.Name);
                return null;
            }


            /**
             * Tables:
             */
            tableConfigList = new List<ReplicatedTableConfiguredTable>
            {
                new ReplicatedTableConfiguredTable
                {
                    TableName = AllTables,
                    ViewName = DefaultViewName,
                    ConvertToRTable = configurationStore.ConvertXStoreTableMode,
                }
            };


            // - lease duration
            leaseDuration = configurationStore.LeaseDuration;

            return new List<View> { view };
        }
    }
}
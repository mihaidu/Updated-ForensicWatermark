﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.WindowsAzure.MediaServices.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ActionsProvider.Entities;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using System.Web;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace ActionsProvider.AMS
{
    class AMSProvider : IAMSProvider
    {
        string _WaterMarkStorageConStr;
        CloudMediaContext _mediaContext;
        CloudStorageAccount _WaterMArkStorageAccount;
        CloudBlobClient _WaterMArkStorageBlobClient;
        CloudStorageAccount _AMSStorageAccount;
        CloudBlobClient _AMSStorageBlobClient;
        string _PUBLISHWATERKEDCOPY;
        int _SASTTL;
        private string GetBlobSasUri(CloudBlobClient stClient, string containerName, string BlobName, SharedAccessBlobPermissions Permissions, int hours)
        {
            //Get a reference to a blob within the container.
            CloudBlobContainer container = stClient.GetContainerReference(containerName);
            container.CreateIfNotExists();
            CloudBlockBlob blob = container.GetBlockBlobReference(BlobName);
            SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(hours),
                Permissions = Permissions
            };
            string sasBlobToken = blob.GetSharedAccessSignature(sasConstraints);
            return blob.Uri + sasBlobToken;
        }
        public AMSProvider(string TenantId, string ClientId, string ClientSecret, Uri AMSApiUri, string WaterMarkStorageConStr, string AMSStorageConStr,string PUBLISHWATERKEDCOPY, int sasTtl)
        {
            AzureAdClientSymmetricKey clientSymmetricKey = new AzureAdClientSymmetricKey(ClientId, ClientSecret);
            var tokenCredentials = new AzureAdTokenCredentials(TenantId, clientSymmetricKey, AzureEnvironments.AzureCloudEnvironment);
            var tokenProvider = new AzureAdTokenProvider(tokenCredentials);
            _mediaContext = new CloudMediaContext(AMSApiUri, tokenProvider);

            //WaterMarkStorage
            _WaterMArkStorageAccount = CloudStorageAccount.Parse(WaterMarkStorageConStr);
            _WaterMArkStorageBlobClient = _WaterMArkStorageAccount.CreateCloudBlobClient();
            _WaterMarkStorageConStr = WaterMarkStorageConStr;
            //AMS Stoarge
            _AMSStorageAccount = CloudStorageAccount.Parse(AMSStorageConStr);
            _AMSStorageBlobClient = _AMSStorageAccount.CreateCloudBlobClient();

            _PUBLISHWATERKEDCOPY = PUBLISHWATERKEDCOPY;


            _SASTTL = sasTtl;
        }
        #region K8S JOB Manifest       
        private VideoInformation ParseGopBitrateFilter(VideoInformation xVideo)
        {
            string partialName = xVideo.FileName.Substring(xVideo.FileName.LastIndexOf('_') + 1);
            xVideo.vbitrate = partialName.Substring(0, partialName.IndexOf('.'));
            partialName = xVideo.FileName.Substring(0, xVideo.FileName.LastIndexOf('_'));
            partialName = partialName.Substring(partialName.LastIndexOf('_') + 1);
            xVideo.videoFilter = $"resize:width={partialName.Substring(0, partialName.IndexOf('x'))},height={partialName.Substring(partialName.IndexOf('x') + 1)}";
            //GOP Size fix from configuration;
            xVideo.gopsize = System.Configuration.ConfigurationManager.AppSettings["gopsize"];
            return xVideo;
        }
        private VideoInformation CreateVideoInformationK28JobNode(string FileName, IAsset theAsset, string AssetLocatorPath)
        {
            VideoInformation videoinfo = new VideoInformation()
            {
                FileName = FileName
            };
            var EncodeFileName = System.Web.HttpUtility.UrlPathEncode(videoinfo.FileName);

            IActionsProvider xman = ActionProviderFactory.GetActionProvider();
            var assetStatus = xman.GetAssetStatus(theAsset.Id);
            if (assetStatus.State == ExecutionStatus.Finished)
            {
                videoinfo.MP4URL = "";
            }
            else
            {
                if (!string.IsNullOrEmpty(AssetLocatorPath))
                {
                    //USE Streaming server URL
                    videoinfo.MP4URL = $"{AssetLocatorPath}{EncodeFileName}";
                }
                else
                {
                    var strConn = System.Configuration.ConfigurationManager.AppSettings[theAsset.StorageAccountName];

                    var account = CloudStorageAccount.Parse(strConn);
                    var client = account.CreateCloudBlobClient();

                    //USES SAS URL
                    videoinfo.MP4URL = GetBlobSasUri(client, theAsset.Uri.Segments[1],EncodeFileName,SharedAccessBlobPermissions.Read,_SASTTL);
                }
            }
            SharedAccessBlobPermissions p =
                        SharedAccessBlobPermissions.Read |
                        SharedAccessBlobPermissions.Write |
                        SharedAccessBlobPermissions.Add |
                        SharedAccessBlobPermissions.Create;
            videoinfo.MMRKURL = GetBlobSasUri(_WaterMArkStorageBlobClient, "mmrkrepo", $"{theAsset.Id}/{EncodeFileName}.mmrk",p,_SASTTL);
            //Update GOP, Bitrate and Video filter
            return ParseGopBitrateFilter(videoinfo);
        }
        private ManifestInfo GetManifest5Jobs(ManifestInfo manifestInfo,IAsset theAsset, IEnumerable<IAssetFile> mp4AssetFiles, string AssetLocatorPath)
        {
            SharedAccessBlobPermissions allAccess =
                        SharedAccessBlobPermissions.Read |
                        SharedAccessBlobPermissions.Write |
                        SharedAccessBlobPermissions.Add |
                        SharedAccessBlobPermissions.Create;

            foreach (var file in mp4AssetFiles.OrderBy(f => f.ContentFileSize))
            {
                VideoInformation video = CreateVideoInformationK28JobNode(file.Name, theAsset, AssetLocatorPath);
                manifestInfo.VideoInformation.Add(video);
                //Watermarked
                foreach (var code in manifestInfo.EnbebedCodes)
                {
                    var wmp4Name = System.Web.HttpUtility.UrlPathEncode(video.FileName);

                    
                    code.MP4WatermarkedURL.Add(new MP4WatermarkedURL()
                    {
                        FileName = video.FileName,
                        WaterMarkedMp4 = GetBlobSasUri(_AMSStorageBlobClient,"watermarked", $"{manifestInfo.JobID}/{theAsset.Id}/{code.EmbebedCode}/{wmp4Name}", allAccess, _SASTTL)
                    });
                }
            }
            return manifestInfo;
        }
        private async Task<string> CreateSharedAccessPolicyAsync(string queueName, string policyName)
        {
            CloudQueue queue = _WaterMArkStorageAccount.CreateCloudQueueClient().GetQueueReference(queueName);
            await queue.CreateIfNotExistsAsync();

            SharedAccessQueuePolicy sharedPolicy = new SharedAccessQueuePolicy()
            {
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(24),
                Permissions =
                    SharedAccessQueuePermissions.Read |
                    SharedAccessQueuePermissions.Add |
                    SharedAccessQueuePermissions.Update |
                    SharedAccessQueuePermissions.ProcessMessages
            };

            string accessSignature = $"{queue.Uri}{queue.GetSharedAccessSignature(sharedPolicy, null)}";
            return accessSignature;

        }
        public async Task<ManifestInfo> GetK8SJobManifestAsync(string AssetID, string JobID, List<string> codes)
        {
            string AssetLocatorPath = "";
            string EmbedderNotificationQueue = await CreateSharedAccessPolicyAsync("embeddernotification", JobID);
            string PreprocessorNotificationQueue = await CreateSharedAccessPolicyAsync("preprocessorout", JobID);
            ManifestInfo myData = new ManifestInfo
            {
                JobID = JobID,
                AssetID = AssetID,
                EmbedderNotificationQueue = EmbedderNotificationQueue,
                PreprocessorNotificationQueue = PreprocessorNotificationQueue,
                //Video information
                VideoInformation = new List<VideoInformation>(),
                //Enbebedcodes
                EnbebedCodes = new List<EnbebedCode>()
            };
            foreach (var code in codes)
            {
                myData.EnbebedCodes.Add(new EnbebedCode()
                {
                    EmbebedCode = code,
                    MP4WatermarkedURL = new List<MP4WatermarkedURL>()
                });
            }
            //AMS
            IAsset currentAsset = null;
            try
            {
                currentAsset = _mediaContext.Assets.Where(a => a.Id == AssetID).FirstOrDefault();
            }
            catch (Exception X)
            {
                throw new Exception($"AssetID {AssetID} not found. Error: {X.Message}");
            }

            var AssetLocator = currentAsset.Locators.Where(l => l.Type == LocatorType.OnDemandOrigin).FirstOrDefault();
            if (AssetLocator == null)
            {
                //Use Blob SAS URL
                AssetLocatorPath = "";
            }
            else
            {
                AssetLocatorPath = AssetLocator.Path;
            }
            IEnumerable<IAssetFile> mp4AssetFiles = currentAsset.AssetFiles.ToList().Where(af => af.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.ContentFileSize);


            myData = GetManifest5Jobs(myData, /*AssetID*/ currentAsset, mp4AssetFiles, AssetLocatorPath);


            return myData;
        }
        #endregion
        #region AMS Creation and Management
        
        public void DeleteAsset(string AssetId)
        {
            IAsset X = _mediaContext.Assets.Where(a => a.Id == AssetId).FirstOrDefault();
            X.Delete();
        }
        public void DeleteWatermakedBlobRenders(string RenderPrefix)
        {
            CloudBlobContainer container = _AMSStorageBlobClient.GetContainerReference("watermarked");

            foreach (IListBlobItem item in container.ListBlobs(RenderPrefix, true))
            {
                if (item.GetType() == typeof(CloudBlockBlob))
                {
                    CloudBlockBlob blob = (CloudBlockBlob)item;
                    blob.Delete();
                    Trace.TraceInformation($"Deleted blob {blob.Name}");
                }
            }
        }
        // Creator locator to enable publishing / streaming of newly created assets.
        private void ProcessCreateLocator(LocatorType locatorType, IAsset asset, AccessPermissions accessPolicyPermissions, TimeSpan accessPolicyDuration, Nullable<DateTime> startTime, string ForceLocatorGUID)
        {
            IAccessPolicy policy;
            try
            {
                policy = _mediaContext.AccessPolicies.Create("AP AMSE", accessPolicyDuration, accessPolicyPermissions);
            }
            catch (Exception ex)
            {
                // log errror
                Trace.TraceError($"ProcessCreateLocator Error {ex.Message}");
                return;
            }

            ILocator locator = null;
            try
            {
                if (locatorType == LocatorType.Sas || string.IsNullOrEmpty(ForceLocatorGUID)) // It's a SAS locator or user does not want to force the GUID if this is a Streaming locator
                {
                    locator = _mediaContext.Locators.CreateLocator(locatorType, asset, policy, startTime);
                }
                else // Streaming locator and user wants to force the GUID
                {
                    locator = _mediaContext.Locators.CreateLocator(ForceLocatorGUID, LocatorType.OnDemandOrigin, asset, policy, startTime);
                }
            }
            catch (Exception ex)
            {
                //log"Error. Could not create a locator for '{0}' (is the asset encrypted, or locators quota has been reached ?)", AssetToP.Name, true);
                Trace.TraceError($"ProcessCreateLocator Error {ex.Message}");
                return;
            }
            if (locator == null) return;

        }
        string ConvertMediaAssetIdToStorageContainerName(String AssetId)
        {
            string AssetPrefix = "nb:cid: UUID:";
            string AssetBlobContainerNamePrefix = "asset-";

            int startIndex = AssetPrefix.Length - 1; // 
            return AssetBlobContainerNamePrefix + AssetId.Substring(startIndex, AssetId.Length - startIndex);
        }
        IAsset GetMediaAssetFromAssetId(string assetId)
        {
            // Use a LINQ Select query to get an asset.
            var assetInstance =
                from a in _mediaContext.Assets
                where a.Id == assetId
                select a;
            // Reference the asset as an IAsset.
            IAsset asset = assetInstance.FirstOrDefault();

            return asset;
        }
        public async Task<WMAssetOutputMessage> CreateEmptyWatermarkedAsset(string ProcessId, string SourceAssetId, string WMEmbedCode)
        {
            WMAssetOutputMessage result = new WMAssetOutputMessage();

            if ((SourceAssetId is null) || (WMEmbedCode is null))
            {
                result.Status = "ERROR";
                result.StatusMessage = "Either Source Asset or WM Embed Code missing.";
                return result;
            }
            try
            {
                // Get a reference to the Source Asset
                IAsset SourceMediaAsset = GetMediaAssetFromAssetId(SourceAssetId);
                string NewAssetName = $"{SourceMediaAsset.Name}-{ProcessId}-{DateTime.Now.Ticks.ToString()}";
                CancellationToken myToken = new CancellationToken();
                IAsset newWatermarkedAsset = await _mediaContext.Assets.CreateAsync(NewAssetName, AssetCreationOptions.None, myToken);
                newWatermarkedAsset.AlternateId = $"{SourceAssetId}-{WMEmbedCode}";
                await newWatermarkedAsset.UpdateAsync();
                result.Status = result.Status = "Finished"; ;
                result.EmbedCode = WMEmbedCode;
                result.WMAssetId = newWatermarkedAsset.Id;
            }
            catch (Exception X)
            {

                result.Status = "ERROR";
                result.StatusMessage = X.Message;
            }
            return result;
        }
        public async Task<WMAssetOutputMessage> GenerateManifest(string SourceAssetId, bool setAsPrimary)
        {
            WMAssetOutputMessage result = new WMAssetOutputMessage();

            if ((SourceAssetId is null))
            {
                result.Status = "ERROR";
                result.StatusMessage = "Either Source Asset ";
                return result;
            }
            IAsset myAsset = GetMediaAssetFromAssetId(SourceAssetId);

            string manifestName = "manifest.ism";
            string videoBase = "      <video src=\"{0}\" />\r\n";
            string AudioBase = "      <audio src = \"{0}\" title = \"{1}\" /> \r\n";
            string switchTxt = "<switch>\r\n";
            string path;

            if (Environment.GetEnvironmentVariable("HOME") != null)
            {
                path = Environment.GetEnvironmentVariable("HOME") + @"\site\wwwroot" + @"\Files\ManifestBase.xml";
            }
            else
            {
                path = @".\Files\ManifestBase.xml";
            }

            string xml = File.ReadAllText(path);

            foreach (IAssetFile file in myAsset.AssetFiles.OrderBy(f => f.ContentFileSize))
            {
                switchTxt += string.Format(videoBase, file.Name);

            }
            switchTxt += string.Format(AudioBase, myAsset.AssetFiles.OrderBy(f => f.ContentFileSize).FirstOrDefault().Name, "English");
            switchTxt += "    </switch>";

            string Manifest = xml.Replace("<switch></switch>", switchTxt);
            //TODO: update Asset container Name
            var assetContainer = _AMSStorageBlobClient.GetContainerReference(myAsset.Id.Replace("nb:cid:UUID:", "asset-"));
            var manifestBlob = assetContainer.GetBlockBlobReference(manifestName);
            await manifestBlob.UploadTextAsync(Manifest);

            var currentFile = await myAsset.AssetFiles.CreateAsync(manifestName, new CancellationToken());
            manifestBlob.FetchAttributes();
            currentFile.ContentFileSize = manifestBlob.Properties.Length;
            currentFile.IsPrimary = setAsPrimary;

            await currentFile.UpdateAsync();

            await myAsset.UpdateAsync();

            result.Status = "OK";
            result.StatusMessage = "Created Manifest";

            return result;
        }
        public async Task<WMAssetOutputMessage> AddWatermarkedMediaFiletoAsset(string WatermarkedAssetId, string WMEmbedCode, string MMRKURL)
        {
            WMAssetOutputMessage result = new WMAssetOutputMessage();
            List<string> detailLog = new List<string>();
            if ((WatermarkedAssetId is null) || (WMEmbedCode is null) || (MMRKURL is null))
            {
                result.Status = "ERROR";
                result.StatusMessage = "Either Source Asset or WM Embed Code missing.";
                return result;
            }
            //Expanded Try area for possible BLOB errors
            try
            {
                IAsset Asset = _mediaContext.Assets.Where(a => a.Id == WatermarkedAssetId).FirstOrDefault();
                detailLog.Add($"GetAsset id={Asset.Id}");
                string containerName = ConvertMediaAssetIdToStorageContainerName(Asset.Id);
                detailLog.Add($"containerName = {containerName}");
                CloudBlobContainer DestinationBlobContainer = _AMSStorageBlobClient.ListContainers().Where(n => n.Name == containerName).FirstOrDefault();
                detailLog.Add($"DestinationBlobContainer = {DestinationBlobContainer}");
                CloudBlockBlob sourceBlob = new CloudBlockBlob(new Uri(MMRKURL));
                detailLog.Add($"sourceBlob = {sourceBlob.Name}");
                // Get a reference to the destination blob (in this case, a new blob).
                string name = HttpUtility.UrlDecode(HttpUtility.UrlDecode(Path.GetFileName(sourceBlob.Uri.AbsolutePath)));
                detailLog.Add($"detination blob name  = {name}");
                CloudBlockBlob destBlob = DestinationBlobContainer.GetBlockBlobReference(name);
                detailLog.Add($"destBlob  = {destBlob.Name}");
                string copyId = null;
                copyId = await destBlob.StartCopyAsync(sourceBlob);
                detailLog.Add($"Finish StartCopyAsync  = {sourceBlob.Name}");

                result.MMRKURLAdded = MMRKURL;
                result.Status = "MMRK File Added";
                result.StatusMessage = destBlob.Name + " added to watermarked asset";
                result.EmbedCode = WMEmbedCode;
                result.WMAssetId = WatermarkedAssetId;

                var currentFile = Asset.AssetFiles.Create(name);
                detailLog.Add($"currentFile  = {currentFile.Name}");
                sourceBlob.FetchAttributes();
                detailLog.Add($"FetchAttributes");
                currentFile.ContentFileSize = sourceBlob.Properties.Length;
                currentFile.Update();
                detailLog.Add($"currentFile.Update()");

                Asset.Update();
                detailLog.Add($"Asset.Update();");

                #region Add Locator to new Media Asset
                if (_PUBLISHWATERKEDCOPY.ToLower()=="true")
                {
                    if (Asset.Locators.Where(l => l.Type == LocatorType.OnDemandOrigin).Count() == 0)
                    {
                        // This could be done at the "end", once for each newly created asset, instead of doing it after each file is added to the newly created asset
                        LocatorType locatorType = LocatorType.OnDemandOrigin;
                        AccessPermissions accessPolicyPermissions = AccessPermissions.Read | AccessPermissions.List;
                        TimeSpan accessPolicyDuration = new TimeSpan(100 * 365, 1, 1, 1, 1);  // 100 years
                        DateTime locaatorStartDate = DateTime.Now;
                        string forceLocatorGuid = null;

                        ProcessCreateLocator(locatorType, Asset, accessPolicyPermissions, accessPolicyDuration, locaatorStartDate, forceLocatorGuid);
                    }
                    else
                    {
                        Trace.TraceInformation($"Assset {Asset.Id} already has OndemandOrigin");
                    }
                }
                #endregion

            }
            catch (StorageException e)
            {
                Trace.TraceError(e.Message);
                //throw;
                result.MMRKURLAdded = MMRKURL;
                result.Status = $"Copy error";
                //Add Blob Info to the error
                result.StatusMessage = $"{MMRKURL} Error {e.Message} {String.Join(Environment.NewLine,detailLog.ToArray())}"; 
                result.EmbedCode = WMEmbedCode;
                result.WMAssetId = WatermarkedAssetId;
               
            }
            finally
            {

            }
            return result;
        }
        #endregion
    }
}

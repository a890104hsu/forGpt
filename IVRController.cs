using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using IVR.Services.FCSService;
using IVR.Services.MDSService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MultiTaxiASR.Models.Entities.ASR;
using MultiTaxiASR.Models.Messages.Common;
using MultiTaxiASR.Models.Messages.System.Responses;
using MultiTaxiASR.Models.ModelCommon.Enums;
using MultiTaxiASR.Services.ASRService;
using MultiTaxiASR.Services.Infrastructure.Extensions;
using MultiTaxiASR.Services.Infrastructure.Interfaces;
using MultiTaxiASR.Services.Infrastructure.Utility;
using Newtonsoft.Json;
using SharpCifs.Smb;
using TGDS.Models.Entities.GIS;
using TGDS.Models.Messages.GIS.Request;
using TGDS.Models.Messages.GIS.Respons;
using TGDS.Models.ModelCommon.Enums;
using TGDS.Services.GISService;

namespace EndPoints.MultiTaxiASRAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class IVRController : ControllerBase
    {
        private readonly ILogger<IVRController> _logger;
        private readonly IConfiguration _config;
        private readonly IIVRService _ivrService;
        private readonly IGISService _gisService;
        private readonly ISysConfigsService _sysConfigsService;
        private readonly IFCSService _fcsService;
        private readonly IMDSService _mdsService;
        private readonly IRedisCacheManager _redisCacheManager;
        protected string LogID;

        public IVRController(ILogger<IVRController> logger, IConfiguration config, IRedisCacheManager redisCacheManager,
           IIVRService ivrService, IGISService gisService, ISysConfigsService sysConfigsService, IFCSService fcsService, IMDSService mdsService)
        {
            _logger = logger;
            _config = config;
            _ivrService = ivrService;
            _gisService = gisService;
            _fcsService = fcsService;
            _mdsService = mdsService;
            _sysConfigsService = sysConfigsService;
            _redisCacheManager = redisCacheManager;
            LogID = SequentialGuidUtil.NewGuid().ToString();
        }

        #region 接收語音辨識並輸出地址
        /// <summary>
        /// 接收語音辨識並輸出地址
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("SpeechAddress"), HttpPost]
        public async Task<BaseResponse<GetAddrRes>> DoSpeechAddress(GetAddrReq data)
        {
            var methodName = "DoSpeechAddress";
            _logger.LogInformation($"[{LogID}]{methodName} Req：{data.serializeJson()}");

            var timeOutSec = _config.GetSection("IdentifyChecked").GetSection("DoSpeechAddressTimeOut").Get<int>();
            var timeoutMilliseconds = timeOutSec * 1000;
            var cts = new CancellationTokenSource(timeoutMilliseconds);
            var resAddr = new GetAddrRes { CRNo = data.CRNo, CustPhone = data.CustPhone, Address = data.Addr };
            try
            {
                // Pro：因為ive有設置超時時間，所以asr也需要 230913：ivr設10秒 asr設9秒
                var longRunningTask = Task.Run(() => _DoSpeechAddress(data));
                if (await Task.WhenAny(longRunningTask, Task.Delay(timeoutMilliseconds)) == longRunningTask)
                {
                    return await longRunningTask;
                }
                else
                {
                    cts.Cancel();
                    _logger.LogInformation($"[{LogID}]{methodName} 操作超時，自動取消操作");
                    return await SpeechAddressResponse(resAddr, 408, CallReasonEnum.超出語音等待時間).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return await SpeechAddressResponse(resAddr, 999, CallReasonEnum.系統錯誤).ConfigureAwait(false);
            }
        }

        private async Task<BaseResponse<GetAddrRes>> _DoSpeechAddress(GetAddrReq data)
        {
            var methodName = "DoSpeechAddress";

            #region 取得開關參數 & UnionPilot預設值
            var nowTime = DateTime.Now;
            var getChecks = _config.GetSection("IdentifyChecked").GetSection("Pilots").Get<List<IdentifyCheckedConfig>>();
            var defaultCustName = _config.GetSection("IdentifyChecked").GetSection("CustName").Get<string>();
            var defaultUnionPilot = "";
            var checkCrossRoads = false; // 是否要判斷交叉路口
            var checkMarkName = false;   // 是否要判斷特殊地標
            var addOneNum = false;       // 是否加上1號判斷 非交叉路口格式但有路口、巷口等字眼(巷口等、弄口等、街口等)
            if (getChecks != null && getChecks.Count > 0)
            {
                var getCheck = getChecks.Where(x => x.FleetType.Trim() == data.FleetType.Trim() && x.Trank_ID == "").FirstOrDefault();
                // 先取得預設值
                if (getCheck != null)
                {
                    checkCrossRoads = getCheck.CrossRoads;
                    checkMarkName = getCheck.MarkName;
                    addOneNum = getCheck.OneNum;
                    defaultUnionPilot = getCheck.UnionPilot;
                }
                // 如果有直接設定的預設值就取，沒有就從DB找
                var getTrank = getChecks.Where(x => x.FleetType.Trim() == data.FleetType.Trim() && x.Trank_ID.Trim() == data.Trank_ID.Trim()).FirstOrDefault();
                if (getTrank != null)
                {
                    checkCrossRoads = getTrank.CrossRoads;
                    checkMarkName = getTrank.MarkName;
                    addOneNum = getTrank.OneNum;
                    defaultUnionPilot = getTrank.UnionPilot;
                }
                else
                {
                    // 取得聯派原則代碼
                    var getMapping = await _fcsService.GetFLEETID_MAPPINGAsync(data.Trank_ID.Trim()).ConfigureAwait(false);
                    if (getMapping != null)
                    {
                        defaultUnionPilot = getMapping.UNION_PILOT;
                    }
                }
            }
            // 取得 乘客姓名&稱謂
            var getCustName = _mdsService.GetMemberName(data.CustPhone);
            if (getCustName != null)
            {
                defaultCustName = getCustName.CUSTNAME + "@" + getCustName.CUSTTITLE;
            }
            #endregion

            #region 判斷、過濾清單

            #region  贅字/交叉路口/替換文字
            // 如果在號的前面，全部移除
            var excessWords0 = _config.GetSection("ExtraWordConfigs").GetSection("ExcessWords0").Get<string[]>();
            // 指定移除的贅字
            var excessWords = _config.GetSection("ExtraWordConfigs").GetSection("ExcessWords").Get<string[]>();
            // 該字串因為有可能含在特殊地標或道路名稱，所以放在第2次查詢才移除
            var excessWords2 = _config.GetSection("ExtraWordConfigs").GetSection("ExcessWords2").Get<string[]>();
            // 連接字
            var joinWords = _config.GetSection("CrossRoadConfigs").GetSection("JoinWords").Get<string[]>();
            // 無法識別的地址單位
            string[] delAddrs = { "鄰", "里", "村" };
            // 交叉路口關鍵字
            var delCrossRoadKeyWord = _config.GetSection("CrossRoadConfigs").GetSection("DelCrossRoadKeyWord").Get<string[]>();
            var crossRoadKeyWord = _config.GetSection("CrossRoadConfigs").GetSection("CrossRoadKeyWord").Get<string[]>();
            // 交叉路口判斷雙道路單位
            var crossRoadTwoWord = _config.GetSection("CrossRoadConfigs").GetSection("CrossRoadTwoWord").Get<string[]>();
            // 需要加1號判斷的關鍵字
            var oneNumReplace = _config.GetSection("CrossRoadConfigs").GetSection("OneNumReplace").Get<string[]>();
            // 巷口等相關字眼
            var noNumKeyWord = _config.GetSection("CrossRoadConfigs").GetSection("NoNumKeyWord").Get<string[]>();
            // 需要替換的巷口等
            var noNumReplace = _config.GetSection("CrossRoadConfigs").GetSection("NoNumReplace").Get<string[]>();
            // 語音辨識會直接顯示中文數字的號
            string[] chineseNum = { "一號", "二號", "三號", "四號", "五號", "六號", "七號", "八號", "九號", "十號" };
            var chineseNumList = new Dictionary<string, long>() { { "一", 1 }, { "二", 2 }, { "三", 3 }, { "四", 4 }, { "五", 5 }, { "六", 6 }, { "七", 7 }, { "八", 8 }, { "九", 9 } };
            // 地標簡稱替換
            var markNameReplaceMain = _config.GetSection("MarkNameConfigs").GetSection("MarkNameReplaceMain").Get<string[]>();
            var markNameReplace = _config.GetSection("MarkNameConfigs").GetSection("MarkNameReplace").Get<string[]>();
            var keyWordReplace = _config.GetSection("MarkNameConfigs").GetSection("KeyWordReplace").Get<string[]>();
            // 指定特殊地標更換名稱
            var markNameReplaceSub = _config.GetSection("MarkNameConfigs").GetSection("MarkNameReplaceSub").Get<string[]>();
            // 檢查是否為英文或數字
            var reg1 = new Regex(@"^[A-Za-z0-9]+$");
            #endregion

            #region 檢查集合
            // City清單：單位為"市"
            var allCity = _config.GetSection("AddressConfigs").GetSection("AllCity").Get<string[]>();
            // City清單：單位為"縣"
            var allCityCounty = _config.GetSection("AddressConfigs").GetSection("AllCityCounty").Get<string[]>();
            // City清單：同時存在"市"、"縣"單位的City
            var allCityTwo = _config.GetSection("AddressConfigs").GetSection("AllCityTwo").Get<string[]>();
            // Dist單位
            var allDist = _config.GetSection("AddressConfigs").GetSection("AllDist").Get<string[]>();
            // Road需排除含區字串
            var distRoad = _config.GetSection("AddressConfigs").GetSection("DistRoad").Get<string[]>();
            // Road有兩個單位的道路名
            var roadTwoName = _config.GetSection("AddressConfigs").GetSection("RoadTwoName").Get<string[]>();
            // Road含有連接字的道路字 "和"(交叉路口用)
            var roadJoinWord = _config.GetSection("CrossRoadConfigs").GetSection("RoadJoinWord").Get<string[]>();
            #endregion

            #endregion

            var newSpeechAddress = AutoMapperHelper.doMapper<GetAddrReq, SpeechAddress>(data);
            var resAddr = new GetAddrRes { CRNo = data.CRNo, CustPhone = data.CustPhone, Address = data.Addr, CheckMarkName = checkMarkName };
            var addrReq = "";

            newSpeechAddress.CustName = defaultCustName;
            newSpeechAddress.UnionPilot = defaultUnionPilot;
            newSpeechAddress.AddrType = 0;
            newSpeechAddress.CreateDate = nowTime;
            newSpeechAddress.ModifyDate = nowTime;
            try
            {
                // 存DB取得AsrId
                var asrId = await _ivrService.CreateSpeechAddressAsync(newSpeechAddress).ConfigureAwait(false);
                newSpeechAddress.AsrId = asrId;
                resAddr.AsrId = asrId;

                #region 語音辨識結果：取得需要拆解的地址
                if (!string.IsNullOrEmpty(data.Addr))
                {
                    addrReq = data.Addr;
                }
                else
                {
                    if (!string.IsNullOrEmpty(data.SpeechPath))
                    {
                        data.CallCloud = new CallCloudReq { File = data.SpeechPath };
                        var callCloud = CallCloud(data.CallCloud);
                        if (callCloud != null && callCloud.StatusCode == 200 && !string.IsNullOrEmpty(callCloud.Result))
                        {
                            addrReq = callCloud.Result;
                        }
                        if (callCloud != null && callCloud.StatusCode == 999)
                        {
                            return await SpeechAddressResponse(resAddr, 999, CallReasonEnum.系統錯誤).ConfigureAwait(false);
                        }
                    }
                }
                newSpeechAddress.IdentifyAddress = addrReq.Trim();
                if (string.IsNullOrEmpty(newSpeechAddress.IdentifyAddress))
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客未出聲).ConfigureAwait(false);
                }
                else
                {
                    // 更新資料
                    newSpeechAddress.ModifyDate = DateTime.Now;
                    var updateColumns = new[] { nameof(SpeechAddress.IdentifyAddress), nameof(SpeechAddress.ModifyDate) }; // 要異動的欄位
                    await _ivrService.UpdateSpeechAddressAsync(newSpeechAddress, updateColumns).ConfigureAwait(false);
                }
                #endregion

                #region 找出地址&座標
                var kw = addrReq.Trim().ToUpper();

                #region 資料基礎判斷
                if (kw.IndexOf(" ") > -1)
                {
                    // 代表說話有停頓
                    var getNewAddr1 = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    if (getNewAddr1.Num.IndexOf(" ") > -1)
                    {
                        var _num = getNewAddr1.Num[getNewAddr1.Num.IndexOf(" ")..].Trim();
                        if (!string.IsNullOrEmpty(_num))
                        {
                            kw = kw.Replace(getNewAddr1.Num, _num);
                        }
                    }
                }
                kw = kw.Replace(" ", "");

                // 有捷運就必須提供幾號出口
                if (!checkMarkName && kw.IndexOf("捷運") > -1 && kw.IndexOf("號") == -1)
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號, 2).ConfigureAwait(false);
                }
                // 判斷是否重複唸
                if (kw.IndexOf("號") > -1)
                {
                    var arrNum = kw.Split("號");
                    if (arrNum.Length > 2)
                    {
                        if (arrNum[0] == arrNum[1])
                        {
                            kw = arrNum[0] + "號";
                        }
                        var arrSect = kw.Split("段");
                        if (arrSect.Length > 2)
                        {
                            if (arrSect[1].Contains(arrSect[0]))
                            {
                                var newKW = "";
                                for (var i = 0; i < arrSect.Length; i++)
                                {
                                    if (i == 0) { continue; }
                                    if (i != arrSect.Length - 1)
                                    {
                                        newKW += arrSect[i] + "段";
                                    }
                                    else
                                    {
                                        newKW += arrSect[i];
                                    }
                                }
                                if (!string.IsNullOrEmpty(newKW))
                                {
                                    kw = newKW;
                                }
                            }
                        }
                    }

                    // 判斷是否有"衖"
                    var getAlley = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    if ((!string.IsNullOrEmpty(getAlley.City) || !string.IsNullOrEmpty(getAlley.Dist)) && (!string.IsNullOrEmpty(getAlley.Road) || !string.IsNullOrEmpty(getAlley.Lane)) &&
                        !string.IsNullOrEmpty(getAlley.Non) && !string.IsNullOrEmpty(getAlley.Num) && getAlley.Num.IndexOf("弄") > -1)
                    {
                        var getAlley2 = SetGISAddress(new SearchGISAddress { Address = getAlley.Num, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getAlley2.Non) && !string.IsNullOrEmpty(getAlley2.Num))
                        {
                            kw = getAlley.City + getAlley.Dist + getAlley.Road + getAlley.Sect + getAlley.Lane + getAlley.Non + getAlley2.Non.Replace("弄", "衖") + getAlley2.Num;
                        }
                    }
                }
                #endregion

                #region 先去掉可能的贅字 ex:樓
                // 切換文字
                foreach (var m in markNameReplaceMain)
                {
                    var arrM = m.Split("|");
                    if (kw.IndexOf(arrM[0]) > -1)
                    {
                        kw = kw.Replace(arrM[0], arrM[1]);
                    }
                }
                if (kw.IndexOf("裡") > -1)
                {
                    // 裡 里 切換
                    var getText = false;
                    string[] noDelText = { "苑裡", "霄裡", "房裡", "裡冷", "夢裡", "這裡", "灣裡", "豐裡", "岸裡", "水裡", "丹裡", "大裡" };
                    foreach (var str in noDelText)
                    {
                        if (kw.IndexOf(str) > -1) { getText = true; break; }
                    }
                    if (!getText) { kw = kw.Replace("裡", "里"); }
                }
                if (kw.IndexOf("派出所") > -1 && kw.IndexOf("市政府警察局") > -1) { kw = kw.Replace("市政府警察局", ""); }
                if (kw.IndexOf("前麵") > -1 && kw.IndexOf("麵攤") == -1 && kw.IndexOf("麵店") == -1) { kw = kw.Replace("前麵", ""); }

                var checkNum = kw.IndexOf("號");
                if (checkNum > -1)
                {
                    foreach (var str in excessWords0)
                    {
                        var _wordidx = kw.IndexOf(str);
                        if (_wordidx > 0 && _wordidx < checkNum)
                        {
                            // 如果贅字在地址之前全部移除
                            kw = kw.Substring(_wordidx + str.Length, kw.Length - _wordidx - str.Length);
                            checkNum = kw.IndexOf("號");
                            break;
                        }
                    }
                    // 如果在後面有 [...] 直接刪
                    string[] delText = { "我要預約", "我要去" };
                    foreach (var str in delText)
                    {
                        var _wordidx = kw.IndexOf(str);
                        if (_wordidx > checkNum)
                        {
                            kw = kw.Substring(0, _wordidx);
                            break;
                        }
                    }
                    if (kw.IndexOf("號門口") > -1 && kw.IndexOf("車站") == -1 && kw.IndexOf("捷運") == -1)
                    {
                        kw = kw.Replace("號門口", "號");
                        checkNum = kw.IndexOf("號");
                    }
                    if (kw.IndexOf("北台中") > -1) { kw = kw.Replace("北台中", ""); }
                    if (kw.IndexOf("洋厝里") > -1) { kw = kw.Replace("洋厝里", ""); }
                }
                if (checkNum == -1)
                {
                    if (kw.IndexOf("弄巷口") > -1) { kw = kw.Replace("弄巷口", "弄弄口"); }
                    if (kw.IndexOf("路巷口") > -1) { kw = kw.Replace("路巷口", "路路口"); }
                }
                // 處理簡稱
                if (kw == "北車") { kw = "台北車站"; }
                if (kw == "省豐") { kw = "豐原醫院"; }
                if (kw == "省桃") { kw = "桃園醫院"; }

                // 移除指定贅字
                foreach (var str in excessWords)
                {
                    kw = kw.Replace(str, "");
                }
                if (string.IsNullOrEmpty(kw) || kw.Length < 3)
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題).ConfigureAwait(false);
                }
                if (kw.IndexOf("雄市") > -1 && kw.IndexOf("高雄市") == -1) { kw = kw.Replace("雄市", "高雄市"); }
                if (kw.IndexOf("桃桃園市") > -1) { kw = kw.Replace("桃桃園市", "桃園市"); }
                if (kw.IndexOf("新板橋") > -1 && kw.IndexOf("號") > -1) { kw = kw.Replace("新板橋", "板橋"); }
                if (kw.IndexOf("交叉路") > -1 && kw.IndexOf("到") > -1) { kw = kw.Replace("到", "與"); }
                if (kw.IndexOf("捷運") > -1 && kw.IndexOf("新莊") > -1 && kw.IndexOf("丹鳳") > -1) { kw = kw.Replace("新莊區", "").Replace("新莊", ""); }
                if (kw.IndexOf("捷運") > -1 && kw.IndexOf("南屯區") > -1) { kw = kw.Replace("南屯區", ""); }
                if (kw.IndexOf("捷運站") > -1)
                {
                    var tempKw = kw.Replace("捷運站", "");
                    if (tempKw.IndexOf("站") > -1) // 兩個"站"
                    {
                        kw = kw.Replace("捷運站", "捷運");
                    }
                }

                checkNum = kw.IndexOf("號");
                #region 該地區地址混亂 || 需要直轉
                var isSPMarkName = false;
                var spKw = kw;
                var spKw2 = "";
                if (kw.IndexOf("中國醫藥") > -1 && (kw.IndexOf("立夫") > -1 || kw.IndexOf("復健") > -1 || kw.IndexOf("第一醫療") > -1)) { spKw = "中國醫藥大學附設醫院立夫醫療大樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && (kw.IndexOf("美德") > -1 || kw.IndexOf("中醫部") > -1)) { spKw = "中國醫藥大學附設醫院-美德醫療大樓中醫部"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("五順街") > -1) { spKw = "中國醫藥大學附設醫院-五順街出入口"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("癌症大樓") > -1 && kw.IndexOf("五義街") > -1) { spKw = "中國附醫癌症中心大樓-五義街出口"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("癌症大樓") > -1) { spKw = "中國附醫癌症中心大樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("急診") > -1 && kw.IndexOf("新竹") == -1 && kw.IndexOf("竹北") == -1) { spKw = "中國醫藥大學附設醫院急診部"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("重症") > -1) { spKw = "中國醫藥大學附設醫院-急重症大樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("北港") > -1) { spKw = "中國醫藥大學北港醫院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && (kw.IndexOf("耳鼻喉科") > -1 || kw.IndexOf("耳喉鼻科") > -1)) { spKw = "中國醫藥大學耳鼻喉科醫學中心大樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("急診") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("竹北") > -1)) { spKw = "中國醫藥大學新竹附設醫院急診室"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("竹北") > -1)) { spKw = "中國醫藥大學新竹附設醫院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("台北") > -1) { spKw = "中國醫藥大學附設醫院臺北分院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國醫藥") > -1 && kw.IndexOf("校本部") == -1 && kw.IndexOf("校區") == -1) { spKw = "中國醫藥大學附設醫院總院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中國藥") > -1 && kw.IndexOf("醫院") > -1 && kw.IndexOf("重症") > -1) { spKw = "中國醫藥大學附設醫院-急重症大樓"; isSPMarkName = true; }

                if (kw == "中山醫學大學") { spKw = "中山醫學大學"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中山醫學") > -1 && kw.IndexOf("急診") > -1) { spKw = "中山醫-急診室"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中山醫學") > -1 && kw.IndexOf("大慶") > -1) { spKw = "中山醫院大慶院區"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中山醫學") > -1 && kw.IndexOf("中興") > -1) { spKw = "中山醫院中興分院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中山醫學") > -1 && kw.IndexOf("台北") > -1) { spKw = "中山醫院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中山醫學") > -1 && kw.IndexOf("醫院") > -1) { spKw = "中山醫院大慶院區"; isSPMarkName = true; }

                if (kw == "屏東火化場" || kw == "屏東火葬場") { spKw = "屏東火葬場"; isSPMarkName = true; }
                if (kw.IndexOf("奇美博物館") > -1) { spKw = "奇美博物館"; isSPMarkName = true; }
                if (kw.IndexOf("慕軒飯店") > -1 || kw.IndexOf("台北慕軒") > -1) { spKw = "慕軒飯店"; isSPMarkName = true; }
                if (kw.IndexOf("燕子口") > -1) { spKw = "燕子口"; isSPMarkName = true; }
                if (kw.IndexOf("澄清湖棒球") > -1) { spKw = "澄清湖棒球場"; isSPMarkName = true; }
                if (kw.IndexOf("新北市") > -1 && kw.IndexOf("地檢署") > -1) { spKw = "新北地方檢察署"; isSPMarkName = true; }
                if (kw.IndexOf("警察") > -1 && kw.IndexOf("林口分局") > -1) { spKw = "警局林口分局"; isSPMarkName = true; }
                if (kw.IndexOf("中影文化城") > -1) { spKw = "中影製片廠"; isSPMarkName = true; }
                if (kw.IndexOf("剝皮寮") > -1 && kw.IndexOf("基金會") == -1 && kw.IndexOf("民宿") == -1) { spKw = "萬華剝皮寮歷史街區"; isSPMarkName = true; }
                if (kw.IndexOf("中友百貨") > -1) { spKw = "中友百貨"; isSPMarkName = true; }
                if (kw.IndexOf("蓋亞莊園") > -1 || kw.IndexOf("蓋婭莊園") > -1) { spKw = "蓋婭莊園"; isSPMarkName = true; }
                if (kw.IndexOf("澄清湖門口") > -1 || kw.IndexOf("澄清胡門口") > -1) { spKw = "澄清湖風景區"; isSPMarkName = true; }
                if (kw.IndexOf("輔大醫院") > -1 && kw.IndexOf("急診") > -1) { spKw = "輔大醫院急診"; isSPMarkName = true; }
                if (kw.IndexOf("輔大醫院") > -1 && kw.IndexOf("急診") == -1) { spKw = "輔大醫院"; isSPMarkName = true; }
                if (kw.IndexOf("輔仁大學") > -1 && kw.IndexOf("醫院") > -1 && kw.IndexOf("急診") > -1) { spKw = "輔大醫院急診"; isSPMarkName = true; }
                if (kw.IndexOf("輔仁大學") > -1 && kw.IndexOf("醫院") > -1 && kw.IndexOf("急診") == -1) { spKw = "輔大醫院"; isSPMarkName = true; }
                if (kw.IndexOf("中原大學") > -1 && kw.IndexOf("郵局") > -1) { spKw = "中原大學郵局"; isSPMarkName = true; }
                if (kw.IndexOf("中原大學") > -1 && (kw.IndexOf("校門") > -1 || kw.IndexOf("大門") > -1)) { spKw = "中原大學大門"; isSPMarkName = true; }
                if (kw.IndexOf("中山") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("醫療") > -1 || kw.IndexOf("醫學大學") > -1) && (kw.IndexOf("汝川") > -1 || kw.IndexOf("如川") > -1)) { spKw = "中山醫院汝川醫療大樓"; isSPMarkName = true; }
                if (kw.IndexOf("寬和宴展館") > -1) { spKw = "寬和宴展館"; isSPMarkName = true; }
                if (kw.IndexOf("萊爾富") > -1 && kw.IndexOf("福惠店") > -1) { spKw = "萊爾富-新莊福慧店"; isSPMarkName = true; }
                if (kw.IndexOf("萊爾富") > -1 && kw.IndexOf("頭份二店") > -1) { spKw = "萊爾富-苗縣頭份二店"; isSPMarkName = true; }
                if (kw.IndexOf("阿官火鍋") > -1 && kw.IndexOf("台中太原") > -1) { spKw = "阿官火鍋-台中太原店"; isSPMarkName = true; }
                if (kw.IndexOf("中華大學") > -1 && kw.IndexOf("進修") == -1 && kw.IndexOf("台北") == -1) { spKw = "中華大學"; isSPMarkName = true; }
                if (kw.IndexOf("彰師大") > -1 && kw.IndexOf("店") == -1) { spKw = "彰化師範大學"; isSPMarkName = true; }
                if (kw.IndexOf("高雄") > -1 && kw.IndexOf("長庚") > -1 && kw.IndexOf("復健") > -1) { spKw = "高雄長庚紀念醫院復健大樓"; isSPMarkName = true; }
                if (kw.IndexOf("高雄") > -1 && kw.IndexOf("長庚") > -1 && kw.IndexOf("兒童") > -1) { spKw = "高雄長庚紀念醫院兒童大樓"; isSPMarkName = true; }
                if (kw.IndexOf("林口") > -1 && kw.IndexOf("長庚") > -1 && kw.IndexOf("急診") > -1) { spKw = "長庚醫院-急診室出入口"; isSPMarkName = true; }
                if (kw.IndexOf("林口") > -1 && kw.IndexOf("長庚") > -1 && kw.IndexOf("質子") > -1) { spKw = "林口長庚醫院質子暨放射治療中心"; isSPMarkName = true; }
                if (kw.IndexOf("高醫") > -1 && kw.IndexOf("急診") > -1) { spKw = "高雄醫學大學附設醫院急診室"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("高雄") > -1 && kw.IndexOf("長庚") > -1) { spKw = "高雄長庚醫院"; isSPMarkName = true; }
                if (kw.IndexOf("故宮") > -1 && kw.IndexOf("圖書館") > -1) { spKw = "故宮博物院圖書館"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("故宮") > -1 && kw.IndexOf("南院") > -1) { spKw = "故宮南院"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("故宮博物") > -1) { spKw = "故宮博物院"; isSPMarkName = true; }
                if (kw.IndexOf("龜佛山廟") > -1 || kw.IndexOf("龜佛山廣福宮") > -1) { spKw = "龜佛山廣福宮"; isSPMarkName = true; }
                if (kw.IndexOf("林口") > -1 && kw.IndexOf("竹林寺") > -1) { spKw = "竹林山觀音寺"; isSPMarkName = true; }
                if (kw.IndexOf("典華") > -1 && kw.IndexOf("館") > -1 && (kw.IndexOf("大直") > -1 || kw.IndexOf("植福路") > -1)) { spKw = "典華-大直館"; isSPMarkName = true; }
                if (kw.IndexOf("典華") > -1 && kw.IndexOf("館") > -1 && (kw.IndexOf("新莊") > -1 || kw.IndexOf("中央路") > -1)) { spKw = "典華-新莊館"; isSPMarkName = true; }
                if (kw.IndexOf("典華") > -1 && kw.IndexOf("幸福") > -1 && kw.IndexOf("大樓") > -1) { spKw = "典華幸福大樓"; isSPMarkName = true; }
                if (kw.IndexOf("開台") > -1 && (kw.IndexOf("台南") > -1 || kw.IndexOf("安平") > -1) && (kw.IndexOf("媽祖廟") > -1 || kw.IndexOf("天后宮") > -1)) { spKw = "安平開台天后宮"; isSPMarkName = true; }
                if (kw.IndexOf("新竹") > -1 && kw.IndexOf("金頓") > -1 && kw.IndexOf("飯店") > -1) { spKw = "金頓國際大飯店-"; isSPMarkName = true; }
                if (kw.IndexOf("士林") > -1 && kw.IndexOf("麥當勞") > -1 && kw.IndexOf("門市") == -1) { spKw = "麥當勞-士林門市"; isSPMarkName = true; }
                if (kw.IndexOf("汐止") > -1 && kw.IndexOf("遠雄") > -1 && kw.IndexOf("A棟") > -1) { spKw = "遠雄U-TOWN-A棟"; isSPMarkName = true; }
                if (kw.IndexOf("晶采大樓") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("中壢") > -1)) { spKw = "中壢京采"; isSPMarkName = true; }
                if (kw.IndexOf("台中監獄") > -1 && kw.IndexOf("舊官邸") > -1) { spKw = "台中監獄典獄長舊官邸"; isSPMarkName = true; }
                if (kw.IndexOf("台中監獄") > -1 && kw.IndexOf("教育館") > -1) { spKw = "台中監獄矯正教育館"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("台中監獄") > -1) { spKw = "矯正署台中監獄"; isSPMarkName = true; }
                if (kw.IndexOf("小巨蛋") > -1 && kw.IndexOf("捷運") == -1 && (kw.IndexOf("南京東路") > -1 || kw.IndexOf("台北") > -1)) { spKw = "台北小巨蛋"; isSPMarkName = true; }
                if (kw.IndexOf("台南") > -1 && kw.IndexOf("安平") > -1 && kw.IndexOf("牙") > -1 && (kw.IndexOf("佳安") > -1 || kw.IndexOf("家安") > -1)) { spKw = "家安牙醫診所"; isSPMarkName = true; }
                if (kw.IndexOf("雄市") > -1 && kw.IndexOf("文山高") > -1) { spKw = "文山高級中學"; isSPMarkName = true; }
                if (kw.IndexOf("僑泰") > -1 && (kw.IndexOf("高級中學") > -1 || kw.IndexOf("高中") > -1)) { spKw = "僑泰高中"; isSPMarkName = true; }
                if (kw.IndexOf("精誠") > -1 && (kw.IndexOf("高級中學") > -1 || kw.IndexOf("高中") > -1)) { spKw = "精誠高中"; isSPMarkName = true; }
                if (kw.IndexOf("苗栗") > -1 && (kw.IndexOf("高級商業") > -1 || kw.IndexOf("職業學校") > -1 || kw.IndexOf("高商") > -1)) { spKw = "苗栗高商"; isSPMarkName = true; }
                if (kw.IndexOf("昭明") > -1 && (kw.IndexOf("小學") > -1 || kw.IndexOf("國小") > -1)) { spKw = "昭明國小"; isSPMarkName = true; }
                if (kw.IndexOf("高雄大學") > -1) { spKw = "高雄大學"; isSPMarkName = true; }
                if (kw.IndexOf("中興大學") > -1) { spKw = "中興大學"; isSPMarkName = true; }
                if (kw.IndexOf("實踐大學") > -1 && (kw.IndexOf("台北") > -1 || kw.IndexOf("臺北") > -1)) { spKw = "實踐大學"; isSPMarkName = true; }
                if (kw.IndexOf("實踐大學") > -1 && kw.IndexOf("高雄") > -1) { spKw = "實踐大學高雄校區"; isSPMarkName = true; }
                if (kw.IndexOf("逢甲大學") > -1 && kw.IndexOf("福星") > -1) { spKw = "逢甲大學福星校區"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("逢甲大學") > -1 && (kw.IndexOf("中科") > -1 || kw.IndexOf("東大路") > -1)) { spKw = "逢甲大學中科校區"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("逢甲大學") > -1) { spKw = "逢甲大學"; isSPMarkName = true; }
                if (kw.IndexOf("高雄") > -1 && (kw.IndexOf("高商") > -1 || kw.IndexOf("高級商業") > -1)) { spKw = "高雄高商"; isSPMarkName = true; }
                if (kw.IndexOf("聖約翰") > -1 && kw.IndexOf("大學") > -1) { spKw = "聖約翰科技大學"; isSPMarkName = true; }
                if (kw.IndexOf("郭綜合") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("急診") > -1)) { spKw = "郭綜合醫院"; isSPMarkName = true; }
                if (kw.IndexOf("嘉義縣稅務大樓") > -1) { spKw = "南區國稅局嘉義縣分局"; isSPMarkName = true; }
                if (kw.IndexOf("新竹高級中學") > -1 || kw.IndexOf("新竹高中") > -1) { spKw = "新竹高中"; isSPMarkName = true; }
                if (kw.IndexOf("永平高級中學") > -1 || kw.IndexOf("永平高中") > -1) { spKw = "永平高中"; isSPMarkName = true; }
                if (kw.IndexOf("成大醫學院") > -1) { spKw = "成功大學醫學院"; isSPMarkName = true; }
                if (kw.IndexOf("中央大學") > -1 && kw.IndexOf("研究") > -1) { spKw = "中央大學-教學研究綜合大樓"; isSPMarkName = true; }
                if (kw.IndexOf("中央大學") > -1 && kw.IndexOf("校門口") > -1) { spKw = "中央大學-校門口"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中央大學") > -1) { spKw = "中央大學"; isSPMarkName = true; }

                #region 捷運/火車站..交通
                if (kw.IndexOf("新北投捷運站") > -1) { spKw = "捷運新北投站"; isSPMarkName = true; }
                if (kw.IndexOf("台北101世貿") > -1 || (kw.IndexOf("捷運") > -1 && kw.IndexOf("台北") > -1 && kw.IndexOf("世貿") > -1))
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("101", ""), IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運台北101/世貿站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運台北101/世貿站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("北投") > -1 && kw.IndexOf("捷運") > -1 && kw.IndexOf("新北投") == -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        if (getNum.Num == "1號")
                        {
                            if (kw.IndexOf("北投路") > -1)
                            {
                                spKw = "捷運北投站1號出口-北投路";
                            }
                            else
                            {
                                spKw = "捷運北投站1號出口-光明路";
                            }
                        }
                        else
                        {
                            spKw = "捷運北投站" + getNum.Num;
                        }
                    }
                    else
                    {
                        spKw = "捷運北投站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("新北投") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運新北投站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運新北投站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("劍南路") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運劍南路站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運劍南路站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("鳳山溪") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運鳳山西站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運鳳山西站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("市政府") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var _city = "台北市信義區&&";
                    if (kw.IndexOf("台中") > -1) { _city = "台中市西屯區&&"; }
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = _city + "捷運市政府站" + getNum.Num;
                    }
                    else
                    {
                        spKw = _city + "捷運市政府站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("港墘") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運港墘站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運港墘站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("民權西路") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運民權西路站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運民權西路站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("體育大學") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("A7", ""), IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運體育大學站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運體育大學站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("新北產業園區") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("Y20", ""), IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運新北產業園區站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運新北產業園區站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("三重區捷運站") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運三重站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運三重站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("小港區捷運站") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運小港站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運小港站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }
                if (kw.IndexOf("台北") > -1 && kw.IndexOf("101") > -1 && kw.IndexOf("捷運") > -1)
                {
                    var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("101", ""), IsCrossRoads = false, doChineseNum = true });
                    if (!string.IsNullOrEmpty(getNum.Num))
                    {
                        spKw = "捷運台北101/世貿站" + getNum.Num;
                    }
                    else
                    {
                        spKw = "捷運台北101/世貿站";
                    }
                    kw = spKw;
                    isSPMarkName = true;
                }

                if (kw.IndexOf("新烏日站") > -1 || (kw.IndexOf("新烏日") > -1 && (kw.IndexOf("車站") > -1 || kw.IndexOf("火車站") > -1))) { spKw = "新烏日火車站"; isSPMarkName = true; }
                if (kw.IndexOf("善化火車站") > -1) { spKw = "善化火車站"; isSPMarkName = true; }
                if (kw.IndexOf("花蓮火車站") > -1) { spKw = "花蓮火車站"; isSPMarkName = true; }
                if (kw.IndexOf("正義火車站") > -1 || (kw.IndexOf("正義路") > -1 && kw.IndexOf("火車站") > -1)) { spKw = "正義火車站"; isSPMarkName = true; }
                if (kw.IndexOf("精武火車站") > -1) { spKw = "精武火車站"; isSPMarkName = true; }
                if (kw.IndexOf("台南火車站") > -1 && kw.IndexOf("後站") > -1) { spKw = "台南火車站後站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("台南火車站") > -1) { spKw = "台南火車站"; isSPMarkName = true; }
                if (kw.IndexOf("高鐵") > -1 && kw.IndexOf("左營") > -1 && kw.IndexOf("高鐵路") == -1) { spKw = "高鐵左營站"; isSPMarkName = true; }
                if (kw.IndexOf("岡山") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "岡山火車站"; isSPMarkName = true; }
                if (kw.IndexOf("屏東車站") > -1 || kw.IndexOf("屏東火車站") > -1) { spKw = "屏東車站"; isSPMarkName = true; }
                if (kw.IndexOf("台中火車站") > -1 || kw.IndexOf("台中車站") > -1) { spKw = "台中火車站"; isSPMarkName = true; }
                if (kw.IndexOf("台中後火車站") > -1 || kw.IndexOf("台中後車站") > -1) { spKw = "台中後火車站"; isSPMarkName = true; }
                if ((kw.IndexOf("沙鹿火車站") > -1 || kw.IndexOf("沙鹿車站") > -1) && kw.IndexOf("後") == -1 && kw.IndexOf("捷運") == -1) { spKw = "沙鹿火車站"; isSPMarkName = true; }
                if (kw.IndexOf("沙鹿") > -1 && (kw.IndexOf("車站") > -1 || kw.IndexOf("火車站") > -1) && kw.IndexOf("後") > -1 && kw.IndexOf("捷運") == -1) { spKw = "沙鹿火車站後站"; isSPMarkName = true; }
                if ((kw.IndexOf("泰安火車站") > -1 || kw.IndexOf("泰安車站") > -1) && kw.IndexOf("舊") == -1 && kw.IndexOf("捷運") == -1) { spKw = "泰安火車站"; isSPMarkName = true; }
                if (kw.IndexOf("泰安") > -1 && (kw.IndexOf("車站") > -1 || kw.IndexOf("火車站") > -1) && kw.IndexOf("舊") > -1 && kw.IndexOf("捷運") == -1) { spKw = "泰安舊車站"; isSPMarkName = true; }
                if (kw.IndexOf("宜蘭火車站") > -1) { spKw = "宜蘭火車站"; isSPMarkName = true; }
                if (kw.IndexOf("高鐵") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("竹北") > -1)) { spKw = "高鐵新竹站"; isSPMarkName = true; }
                if (kw.IndexOf("新竹") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("竹北") == -1 && kw.IndexOf("捷運") == -1) { spKw = "新竹火車站"; isSPMarkName = true; }
                if (kw.IndexOf("竹北") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後站") == -1 && kw.IndexOf("捷運") == -1) { spKw = "竹北火車站"; isSPMarkName = true; }
                if (kw.IndexOf("竹北") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後站") > -1 && kw.IndexOf("捷運") == -1) { spKw = "竹北火車站後站"; isSPMarkName = true; }
                if (kw.IndexOf("大慶") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "大慶火車站"; isSPMarkName = true; }
                if (kw.IndexOf("桃園") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("前站") == -1 && kw.IndexOf("後站") == -1 && kw.IndexOf("捷運") == -1) { spKw = "桃園火車站"; isSPMarkName = true; }
                if (kw.IndexOf("桃園") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("前站") > -1 && kw.IndexOf("捷運") == -1) { spKw = "桃園火車站-前站"; isSPMarkName = true; }
                if (kw.IndexOf("桃園") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後站") > -1 && kw.IndexOf("捷運") == -1) { spKw = "桃園火車站-後站"; isSPMarkName = true; }
                if (kw.IndexOf("台中") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後") > -1 && kw.IndexOf("捷運") == -1) { spKw = "台中後火車站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("台中") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "台中火車站"; isSPMarkName = true; }
                if (kw.IndexOf("苗栗") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("西") > -1 && kw.IndexOf("捷運") == -1) { spKw = "苗栗火車站-西站"; isSPMarkName = true; }
                if (kw.IndexOf("苗栗") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("東") > -1 && kw.IndexOf("捷運") == -1) { spKw = "苗栗火車站-東站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("苗栗") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "苗栗火車站"; isSPMarkName = true; }
                if (kw.IndexOf("內壢") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後") > -1 && kw.IndexOf("捷運") == -1) { spKw = "內壢火車站後站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("內壢") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "內壢火車站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中壢") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("後") > -1 && kw.IndexOf("捷運") == -1) { spKw = "中壢火車站後站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("中壢") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "中壢火車站"; isSPMarkName = true; }
                if (kw.IndexOf("新左營") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "新左營火車站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("左營") > -1 && (kw.IndexOf("火車站") > -1 || kw.IndexOf("車站") > -1) && kw.IndexOf("捷運") == -1) { spKw = "左營火車站"; isSPMarkName = true; }
                if (kw.IndexOf("雲林高鐵") > -1) { spKw = "高鐵雲林站"; isSPMarkName = true; }
                if (kw.IndexOf("造橋車站") > -1) { spKw = "造橋火車站"; isSPMarkName = true; }
                if (kw.IndexOf("田中高鐵站") > -1) { spKw = "高鐵彰化站"; isSPMarkName = true; }
                if (kw.IndexOf("埔里轉運站") > -1) { spKw = "南投客運-埔里轉運站"; isSPMarkName = true; }
                if (kw.IndexOf("小港機場") > -1 && kw.IndexOf("國內") > -1) { spKw = "高雄機場-國內航廈"; isSPMarkName = true; }
                if (kw.IndexOf("和欣") > -1 && kw.IndexOf("客運") > -1 && kw.IndexOf("新營") > -1) { spKw = "新營和欣客運轉運站"; isSPMarkName = true; }
                if (kw.IndexOf("統聯") > -1 && kw.IndexOf("客運") > -1 && kw.IndexOf("楠梓") > -1) { spKw = "統聯客運-楠梓站"; isSPMarkName = true; }

                #endregion

                if (kw.IndexOf("北屯") > -1 && (kw.IndexOf("監理站") > -1 || kw.IndexOf("監理所") > -1)) { spKw = "北屯路監理所"; isSPMarkName = true; }
                if (kw.IndexOf("豐原") > -1 && (kw.IndexOf("監理站") > -1 || kw.IndexOf("監理所") > -1)) { spKw = "豐原監理站"; isSPMarkName = true; }
                if (!isSPMarkName && (kw.IndexOf("台中") > -1 || kw.IndexOf("大肚") > -1) && (kw.IndexOf("監理站") > -1 || kw.IndexOf("監理所") > -1)) { spKw = "台中區監理所"; isSPMarkName = true; }
                if (kw.IndexOf("台中") > -1 && kw.IndexOf("文心") > -1 && kw.IndexOf("拖吊") > -1) { spKw = "文心拖吊場"; isSPMarkName = true; }
                if (kw.IndexOf("華航") > -1 && kw.IndexOf("派遣中心") > -1 && (kw.IndexOf("台北") > -1 || kw.IndexOf("松山") > -1)) { spKw = "華航派遣中心"; isSPMarkName = true; }
                if (kw.IndexOf("背包41青年旅館") > -1 && kw.IndexOf("台中") > -1) { spKw = "背包41青年旅館-台中館"; isSPMarkName = true; }
                if (kw.IndexOf("背包41青年旅館") > -1 && kw.IndexOf("高雄") > -1) { spKw = "背包41青年旅館-高雄館"; isSPMarkName = true; }
                if (kw.IndexOf("路得行旅") > -1 && kw.IndexOf("台中") > -1) { spKw = "路得行旅國際青年旅館台中站前館"; isSPMarkName = true; }
                if (kw.IndexOf("路得行旅") > -1 && kw.IndexOf("台東") > -1 && kw.IndexOf("2館") == -1) { spKw = "路得行旅"; isSPMarkName = true; }
                if (kw.IndexOf("路得行旅") > -1 && kw.IndexOf("台東") > -1 && kw.IndexOf("2館") > -1) { spKw = "路得行旅-台東2館"; isSPMarkName = true; }
                if (kw.IndexOf("橙舍背包") > -1 && (kw.IndexOf("台北") > -1 || kw.IndexOf("西門") > -1)) { spKw = "橙舍背包客國際青年旅館-台北西門館"; isSPMarkName = true; }
                if (kw.IndexOf("逗號動物醫院") > -1 && kw.IndexOf("南崁") > -1) { spKw = "逗號動物醫院-南崁院區"; isSPMarkName = true; }
                if (kw.IndexOf("裕昌汽車") > -1 && kw.IndexOf("一心店") > -1) { spKw = "NISSAN汽車-裕昌一心服務據點"; isSPMarkName = true; }
                if (kw.IndexOf("汐止好料理") > -1) { spKw = "好料理餐廳"; isSPMarkName = true; }
                if (kw.IndexOf("迴龍") > -1 && kw.IndexOf("樂生") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("療養院") > -1)) { spKw = "樂生療養院迴龍院區"; isSPMarkName = true; }
                if (kw.IndexOf("桃園療養院") > -1) { spKw = "桃園療養院"; isSPMarkName = true; }
                if (kw.IndexOf("文心秀泰") > -1) { spKw = "秀泰生活-台中文心店"; isSPMarkName = true; }
                if (kw.IndexOf("為恭醫院") > -1 && (kw.IndexOf("苗栗") > -1 || kw.IndexOf("頭份") > -1)) { spKw = "為恭紀念醫院信義院區"; isSPMarkName = true; }
                if (kw.IndexOf("旗山醫院") > -1) { spKw = "旗山醫院"; isSPMarkName = true; }
                if (kw.IndexOf("七星潭風景區") > -1) { spKw = "七星潭遊客中心"; isSPMarkName = true; }
                if (kw.IndexOf("內政部國土署") > -1) { spKw = "內政部國土管理署"; isSPMarkName = true; }
                if (kw.IndexOf("三重") > -1 && kw.IndexOf("區公所") > -1) { spKw = "三重區公所"; isSPMarkName = true; }
                if (kw.IndexOf("小港") > -1 && kw.IndexOf("區公所") > -1) { spKw = "小港區公所"; isSPMarkName = true; }
                if (kw.IndexOf("大社") > -1 && kw.IndexOf("區公所") > -1) { spKw = "大社區公所"; isSPMarkName = true; }
                if (kw.IndexOf("大肚") > -1 && kw.IndexOf("區公所") > -1) { spKw = "大肚區公所"; isSPMarkName = true; }
                if (kw.IndexOf("潮州") > -1 && kw.IndexOf("戶政事務所") > -1) { spKw = "潮州戶政事務所"; isSPMarkName = true; }
                if (kw.IndexOf("鼓山") > -1 && kw.IndexOf("戶政事務所") > -1 && kw.IndexOf("第二") > -1) { spKw = "鼓山區戶政事務所第二辦公處"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("鼓山") > -1 && kw.IndexOf("戶政事務所") > -1) { spKw = "鼓山區戶政事務所"; isSPMarkName = true; }
                if (kw.IndexOf("遊園路") > -1 && kw.IndexOf("監理站") > -1) { spKw = "台中區監理所"; isSPMarkName = true; }
                if (kw.IndexOf("台中") > -1 && kw.IndexOf("監理站") > -1 && kw.IndexOf("豐原") == -1) { spKw = "台中市監理站"; isSPMarkName = true; }
                if (kw.IndexOf("台南") > -1 && kw.IndexOf("監理站") > -1 && kw.IndexOf("麻豆") == -1 && kw.IndexOf("新營") == -1) { spKw = "台南監理站"; isSPMarkName = true; }
                if (kw.IndexOf("南亞大潤發") > -1) { spKw = "大潤發-新竹湳雅店"; isSPMarkName = true; }
                if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("福容") > -1 && (kw.IndexOf("飯店") > -1 || kw.IndexOf("酒店") > -1)) { spKw = "嘉義福容voco酒店"; isSPMarkName = true; }
                if (kw.IndexOf("昇恆昌") > -1 && kw.IndexOf("內湖") > -1) { spKw = "昇恆昌免稅商店-內湖旗艦店"; isSPMarkName = true; }
                if (kw.IndexOf("工研院") > -1 && kw.IndexOf("光復院區") > -1) { spKw = "工業技術研究院-光復院區"; isSPMarkName = true; }
                if (kw.IndexOf("瑞芳") > -1 && kw.IndexOf("衛生所") > -1) { spKw = "瑞芳區衛生所"; isSPMarkName = true; }
                if (kw.IndexOf("大直高中") > -1) { spKw = "大直高中"; isSPMarkName = true; }
                if (kw.IndexOf("北投") > -1 && kw.IndexOf("法藏寺") > -1) { spKw = "法藏寺"; isSPMarkName = true; }
                if (kw.IndexOf("新店") > -1 && kw.IndexOf("能仁家商") > -1) { spKw = "能仁家商"; isSPMarkName = true; }
                if ((kw.IndexOf("新民校區") > -1 || kw.IndexOf("新明校區") > -1) && (kw.IndexOf("加大") > -1 || kw.IndexOf("嘉大") > -1 || kw.IndexOf("嘉義大學") > -1)) { spKw = "嘉義大學新民校區"; isSPMarkName = true; }
                if (kw.IndexOf("南紡夢時代") > -1) { spKw = "南紡購物中心"; isSPMarkName = true; }
                if (kw.IndexOf("漢神") > -1 && kw.IndexOf("巨蛋") > -1 && (kw.IndexOf("購物") > -1 || kw.IndexOf("百貨") > -1)) { spKw = "漢神巨蛋購物廣場"; isSPMarkName = true; }
                if (kw.IndexOf("國軍左營總醫院") > -1) { spKw = "高雄總醫院左營分院"; isSPMarkName = true; }
                if (kw.IndexOf("義大醫院") > -1) { spKw = "義大醫院"; isSPMarkName = true; }
                if (kw.IndexOf("新光醫院") > -1) { spKw = "新光醫院"; isSPMarkName = true; }
                if (kw.IndexOf("竹科科技生活館") > -1) { spKw = "新竹科學園區科技生活館"; isSPMarkName = true; }
                if (kw.IndexOf("陸軍") > -1 && (kw.IndexOf("官校") > -1 || kw.IndexOf("軍校") > -1) && (kw.IndexOf("高雄") > -1 || kw.IndexOf("鳳山") > -1)) { spKw = "陸軍官校"; isSPMarkName = true; }
                if (kw.IndexOf("黃埔") > -1 && (kw.IndexOf("官校") > -1 || kw.IndexOf("軍校") > -1) && (kw.IndexOf("高雄") > -1 || kw.IndexOf("鳳山") > -1)) { spKw = "黃埔軍校"; isSPMarkName = true; }
                if (kw.IndexOf("香格里拉") > -1 && kw.IndexOf("台南") > -1) { spKw = "台南遠東香格里拉"; isSPMarkName = true; }
                if (kw.IndexOf("香格里拉") > -1 && kw.IndexOf("台北") > -1 && kw.IndexOf("遠東") == -1 && kw.IndexOf("飯店") == -1 && kw.IndexOf("酒店") == -1) { spKw = "台北香格里拉"; isSPMarkName = true; }
                if (kw.IndexOf("遠東國際大飯店") > -1 || kw.IndexOf("台北遠東飯店") > -1) { spKw = "台北遠東香格里拉"; isSPMarkName = true; }
                if (kw.IndexOf("國立中山大學") > -1) { spKw = "中山大學-校門口"; isSPMarkName = true; }
                if (kw.IndexOf("科園國小") > -1) { spKw = "科園國小"; isSPMarkName = true; }
                if (kw.IndexOf("嘉義國中") > -1) { spKw = "嘉義國中"; isSPMarkName = true; }
                if (kw.IndexOf("傳藝") > -1 && (kw.IndexOf("國立") > -1 || kw.IndexOf("宜蘭") > -1) && kw.IndexOf("南區") > -1) { spKw = "傳統藝術中心南區入口"; isSPMarkName = true; }
                if (kw.IndexOf("傳藝") > -1 && (kw.IndexOf("國立") > -1 || kw.IndexOf("宜蘭") > -1) && kw.IndexOf("中央") > -1) { spKw = "傳統藝術中心中央入口"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("傳藝") > -1 && kw.IndexOf("中心") > -1 && (kw.IndexOf("國立") > -1 || kw.IndexOf("宜蘭") > -1)) { spKw = "傳統藝術中心"; isSPMarkName = true; }
                if (kw.IndexOf("員林") > -1 && kw.IndexOf("統聯") > -1 && kw.IndexOf("二林") > -1) { spKw = "統聯客運-員林客運二林站"; isSPMarkName = true; }
                if (kw.IndexOf("員林") > -1 && kw.IndexOf("統聯") > -1 && kw.IndexOf("溪湖") > -1) { spKw = "統聯客運-員林客運二林站"; isSPMarkName = true; }
                if (kw.IndexOf("員林") > -1 && kw.IndexOf("統聯") > -1 && kw.IndexOf("-") == -1) { spKw = "統聯客運-員林轉運站"; isSPMarkName = true; }
                if (kw.IndexOf("屏東") > -1 && kw.IndexOf("海生館") > -1) { spKw = "海洋生物博物館"; isSPMarkName = true; }
                if (kw.IndexOf("台北") > -1 && (kw.IndexOf("凱薩") > -1 || kw.IndexOf("凱撒") > -1) && kw.IndexOf("飯店") > -1) { spKw = "台北凱撒大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("板橋") > -1 && (kw.IndexOf("凱薩") > -1 || kw.IndexOf("凱撒") > -1) && kw.IndexOf("飯店") > -1) { spKw = "板橋凱撒大飯店"; isSPMarkName = true; }
                if ((kw.IndexOf("墾丁") > -1 || kw.IndexOf("屏東") > -1 || kw.IndexOf("恆春") > -1) && (kw.IndexOf("凱薩") > -1 || kw.IndexOf("凱撒") > -1) && kw.IndexOf("飯店") > -1) { spKw = "墾丁凱撒大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("中壢") > -1 && kw.IndexOf("天成醫院") > -1) { spKw = "天晟醫院"; isSPMarkName = true; }
                if (kw.IndexOf("亞東醫院") > -1 && kw.IndexOf("急診") > -1) { spKw = "亞東醫院-急診部"; isSPMarkName = true; }
                if (kw.IndexOf("兆豐") > -1 && kw.IndexOf("雙和") > -1 && (kw.IndexOf("分行") > -1 || kw.IndexOf("銀行") > -1)) { spKw = "兆豐銀行-雙和分行"; isSPMarkName = true; }
                if (kw.IndexOf("三峽殯儀館") > -1 || kw.IndexOf("三峽火葬場") > -1 || kw.IndexOf("三峽火化場") > -1) { spKw = "新北市立殯儀館附設火化場"; isSPMarkName = true; }
                if (kw.IndexOf("新竹殯儀館") > -1) { spKw = "新竹市立殯儀館-追思園"; isSPMarkName = true; }
                if (kw.IndexOf("壯圍生命紀念館") > -1) { spKw = "壯圍鄉生命紀念館"; isSPMarkName = true; }
                if (kw.IndexOf("基隆火葬場") > -1) { spKw = "基隆市立殯葬管理所火化場"; isSPMarkName = true; }
                if (kw.IndexOf("惠來殯儀館") > -1) { spKw = "惠來火化場"; isSPMarkName = true; }
                if (kw.IndexOf("花蓮市殯儀館") > -1) { spKw = "花蓮市立殯儀館"; isSPMarkName = true; }
                if (kw.IndexOf("第一殯儀館") > -1 && kw.IndexOf("高雄") > -1) { spKw = "高市殯葬管理處服務中心"; isSPMarkName = true; }
                if (kw.IndexOf("新營") > -1 && (kw.IndexOf("殯儀館") > -1 || kw.IndexOf("殯葬專區") > -1)) { spKw = "新營福園殯葬專區"; isSPMarkName = true; }
                if (kw.IndexOf("石牌好樂迪") > -1) { spKw = "好樂迪-台北石牌店"; isSPMarkName = true; }
                if (kw.IndexOf("樹林衛生所") > -1) { spKw = "樹林區衛生所"; isSPMarkName = true; }
                if (kw.IndexOf("台北如溪飯店") > -1) { spKw = "ILLUME茹曦酒店"; isSPMarkName = true; }
                if ((kw.IndexOf("基隆") > -1 || kw.IndexOf("七堵") > -1) && kw.IndexOf("百福") > -1 && kw.IndexOf("消防") > -1 && kw.IndexOf("分隊") > -1) { spKw = "消防局百福分隊"; isSPMarkName = true; }
                if (kw.IndexOf("台中") > -1 && kw.IndexOf("經典酒店") > -1) { spKw = "台中金典酒店"; isSPMarkName = true; }
                if (kw.IndexOf("奇岩一號") > -1 && kw.IndexOf("萬豪店") > -1) { spKw = "奇岩一號-旗艦餐廳"; isSPMarkName = true; }
                if (kw.IndexOf("萊爾富") > -1 && kw.IndexOf("沙鹿") > -1 && (kw.IndexOf("中行") > -1 || kw.IndexOf("中航") > -1)) { spKw = "萊爾富-沙鹿中航店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("彰化") > -1 && kw.IndexOf("新天祥") > -1) { spKw = "全家-彰化新天祥店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("竹南") > -1 && (kw.IndexOf("忠義") > -1 || kw.IndexOf("中義") > -1)) { spKw = "全家-竹南中義店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("大河店") > -1) { spKw = "全家-台中大河店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("全順店") > -1) { spKw = "全家-台南全順店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("龍勝店") > -1) { spKw = "全家-龍井龍勝店"; isSPMarkName = true; }
                if (kw.IndexOf("全家") > -1 && kw.IndexOf("南港") > -1 && (kw.IndexOf("公仔") > -1 || kw.IndexOf("公宅") > -1)) { spKw = "全家-南港公宅店"; isSPMarkName = true; }
                if (kw.IndexOf("南北樓") > -1 && (kw.IndexOf("前鎮") > -1 || kw.IndexOf("林森") > -1)) { spKw = "南北樓中餐廳-林森店"; isSPMarkName = true; }
                if (kw.IndexOf("南北樓") > -1 && (kw.IndexOf("三民") > -1 || kw.IndexOf("建工") > -1)) { spKw = "南北樓中餐廳-建工店"; isSPMarkName = true; }
                if (kw.IndexOf("民雄") > -1 && kw.IndexOf("馬自達") > -1 && kw.IndexOf("保養廠") > -1) { spKw = "Mazda汽車-瑞達汽車民雄廠維修中心"; isSPMarkName = true; }
                if (kw.IndexOf("東區之星") > -1 && kw.IndexOf("KTV") > -1) { spKw = "東區之星自助式KTV"; isSPMarkName = true; }
                if (kw.IndexOf("潮港城海鮮") > -1) { spKw = "潮港城海鮮樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("潮港城") > -1 && kw.IndexOf("花蓮") == -1 && kw.IndexOf("大甲") == -1) { spKw = "潮港城國際美食館"; isSPMarkName = true; }
                if (kw == "小人國") { spKw = "小人國主題樂園"; isSPMarkName = true; }
                if (kw == "兒童樂園" || kw == "台北兒童新樂園" || kw.IndexOf("市立兒童新樂園") > -1) { spKw = "台北兒童新樂園"; isSPMarkName = true; }
                if (kw == "木柵動物園" || kw == "台北動物園" || kw == "台北市立動物園") { spKw = "台北市立動物園"; isSPMarkName = true; }
                if (kw.IndexOf("內惟藝術中心") > -1 && kw.IndexOf("輕軌") == -1) { spKw = "內惟藝術中心"; isSPMarkName = true; }
                if (kw.IndexOf("東元醫院") > -1) { spKw = "東元醫院"; isSPMarkName = true; }
                if (kw.IndexOf("恩主公醫院") > -1) { spKw = "恩主公醫院"; isSPMarkName = true; }
                if (kw.IndexOf("鳳山長庚") > -1) { spKw = "鳳山醫院"; isSPMarkName = true; }
                if (kw.IndexOf("新菩提醫院") > -1) { spKw = "新菩提醫院"; isSPMarkName = true; }
                if (kw.IndexOf("和信") > -1 && kw.IndexOf("癌") > -1 && (kw.IndexOf("中心") > -1 || kw.IndexOf("醫院") > -1)) { spKw = "和信治癌中心醫院"; isSPMarkName = true; }
                if (kw.IndexOf("國統國際") > -1 && kw.IndexOf("新園") > -1) { spKw = "國統國際-新園工廠"; isSPMarkName = true; }
                if (kw.IndexOf("自來水") > -1 && kw.IndexOf("第四區") > -1) { spKw = "台灣自來水-第四區管理處"; isSPMarkName = true; }
                if (kw.IndexOf("健行") > -1 && (kw.IndexOf("大學") > -1 || kw.IndexOf("科大") > -1)) { spKw = "健行科技大學"; isSPMarkName = true; }
                if (kw.IndexOf("衛生所") > -1 && kw.IndexOf("區") == -1) { spKw = kw.Replace("衛生所", "區衛生所"); isSPMarkName = true; }
                if (kw.IndexOf("貴族世家") > -1 && kw.IndexOf("昆陽店") > -1) { spKw = "貴族世家-台北昆陽店"; isSPMarkName = true; }
                if (kw.IndexOf("中油") > -1 && kw.IndexOf("清水站") > -1) { spKw = "中油-清水站"; isSPMarkName = true; }
                if (kw.IndexOf("老新台菜") > -1 && kw.IndexOf("十全") > -1) { spKw = "老新台菜-十全店"; isSPMarkName = true; }
                if (kw.IndexOf("老新台菜") > -1 && kw.IndexOf("九如") > -1) { spKw = "老新台菜-九如店"; isSPMarkName = true; }
                if (kw.IndexOf("北一女中") > -1) { spKw = "北一女中"; isSPMarkName = true; }
                if (kw.IndexOf("金悅軒") > -1) { spKw = "金悅軒"; isSPMarkName = true; }
                if (kw.IndexOf("樂天宮") > -1 && (kw.IndexOf("新北") > -1 || kw.IndexOf("中和") > -1)) { spKw = "中和樂天宮"; isSPMarkName = true; }
                if (kw.IndexOf("樂天宮") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("蘆竹") > -1)) { spKw = "桃園樂天宮"; isSPMarkName = true; }
                if (kw.IndexOf("台大") > -1 && kw.IndexOf("體育館") > -1) { spKw = "台灣大學-台大體育館"; isSPMarkName = true; }
                if ((kw.IndexOf("桃喜") > -1 || kw.IndexOf("桃禧") > -1) && kw.IndexOf("二館") > -1 && (kw.IndexOf("飯店") > -1 || kw.IndexOf("酒店") > -1)) { spKw = "桃禧航空城酒店-二館"; isSPMarkName = true; }
                if ((kw.IndexOf("桃喜") > -1 || kw.IndexOf("桃禧") > -1) && kw.IndexOf("二館") > -1 && (kw.IndexOf("沐悅") > -1 || kw.IndexOf("SPA") > -1)) { spKw = "桃禧航空城酒店沐悅SPA會館"; isSPMarkName = true; }
                if (!isSPMarkName && (kw.IndexOf("桃喜") > -1 || kw.IndexOf("桃禧") > -1) && (kw.IndexOf("飯店") > -1 || kw.IndexOf("酒店") > -1)) { spKw = "桃禧航空城酒店-新館"; isSPMarkName = true; }
                if (kw.IndexOf("薇閣國小") > -1) { spKw = "薇閣小學"; isSPMarkName = true; }
                if (kw.IndexOf("台大") > -1 && kw.IndexOf("急診") > -1 && kw.IndexOf("徐州路") > -1) { spKw = "台大醫院急診部-徐州路上"; isSPMarkName = true; }
                if (kw.IndexOf("寶麗金") > -1 && kw.IndexOf("市政") > -1) { spKw = "寶麗金婚宴會館-市政店"; isSPMarkName = true; }
                if (kw.IndexOf("寶麗金") > -1 && kw.IndexOf("崇德") > -1) { spKw = "寶麗金餐飲集團-崇德店"; isSPMarkName = true; }
                if (kw.IndexOf("TOYOTA") > -1 && kw.IndexOf("南新竹") > -1) { spKw = "TOYOTA汽車-桃苗豐田南新竹服務廠"; isSPMarkName = true; }
                if (kw.IndexOf("致穩") > -1 && (kw.IndexOf("飯店") > -1 || kw.IndexOf("商旅") > -1)) { spKw = "致穩人文商旅"; isSPMarkName = true; }
                if (kw.IndexOf("公益派出所") > -1) { spKw = "公益派出所"; isSPMarkName = true; }
                if (kw.IndexOf("沙鹿") > -1 && (kw.IndexOf("嘉宏") > -1 || kw.IndexOf("佳弘") > -1) && (kw.IndexOf("洗腎") > -1 || kw.IndexOf("診所") > -1)) { spKw = "佳弘診所"; isSPMarkName = true; }
                if (kw.IndexOf("長庚") > -1 && kw.IndexOf("村") > -1 && kw.IndexOf("A") > -1 && (kw.IndexOf("養老") > -1 || kw.IndexOf("養生") > -1)) { spKw = "長庚養生文化村A棟"; isSPMarkName = true; }
                if (kw.IndexOf("台中") > -1 && kw.IndexOf("國立") > -1 && kw.IndexOf("美術館") > -1) { spKw = "台灣美術館"; isSPMarkName = true; }
                if (kw.IndexOf("新復珍") > -1) { spKw = "新復珍商行"; isSPMarkName = true; }
                if (kw.IndexOf("LUXGEN") > -1 && kw.IndexOf("中清") > -1 && kw.IndexOf("服務") > -1) { spKw = "LUXGEN汽車-中清服務廠"; isSPMarkName = true; }
                if (kw.IndexOf("台北市政府") > -1 && kw.IndexOf("西大門") > -1) { spKw = "台北市政府-市府路出入口"; isSPMarkName = true; }
                if (kw.IndexOf("工商展覽中心") > -1 && (kw.IndexOf("五股") > -1 || kw.IndexOf("新北市") > -1)) { spKw = "新北市工商展覽中心"; isSPMarkName = true; }
                if (kw.IndexOf("台鐵") > -1 && kw.IndexOf("竹中站") > -1) { spKw = "竹中車站"; isSPMarkName = true; }
                if (kw.IndexOf("高雄醫學大學") > -1 && kw.IndexOf("同盟") > -1 && kw.IndexOf("醫院") == -1) { spKw = "高雄醫學大學-同盟路校門"; isSPMarkName = true; }
                if (kw.IndexOf("諾富特") > -1 && kw.IndexOf("飯店") > -1) { spKw = "諾富特華航桃園機場飯店"; isSPMarkName = true; }
                if ((kw.IndexOf("宏其") > -1 && kw.IndexOf("大興西路") > -1) || kw.IndexOf("宏其生基") > -1) { spKw = "宏其生基西醫中醫生殖中心"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("宏其") > -1 && (kw.IndexOf("婦產") > -1 || kw.IndexOf("婦幼") > -1 || kw.IndexOf("生殖") > -1)) { spKw = "宏其婦幼醫院"; isSPMarkName = true; }
                if (kw.IndexOf("新竹") > -1 && kw.IndexOf("國泰") > -1 && (kw.IndexOf("健檢") > -1 || kw.IndexOf("健康") > -1)) { spKw = "國泰健康管理新竹中心"; isSPMarkName = true; }
                if (kw.IndexOf("國軍802") > -1 && kw.IndexOf("醫院") > -1) { spKw = "國軍高雄總醫院"; isSPMarkName = true; }
                if (kw.IndexOf("7-ELEVEN") > -1 && kw.IndexOf("龍坑") > -1) { spKw = "7－ELEVEN-龍坑門市"; isSPMarkName = true; }
                if (kw.IndexOf("宏匯") > -1 && kw.IndexOf("中央路") > -1) { spKw = "宏匯廣場-中央路出口"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("宏匯") > -1 && (kw.IndexOf("廣場") > -1 || kw.IndexOf("新莊") > -1 || kw.IndexOf("新北大道") > -1)) { spKw = "新莊宏匯廣場"; isSPMarkName = true; }
                if (kw.IndexOf("古華") > -1 && kw.IndexOf("飯店") > -1) { spKw = "古華花園飯店"; isSPMarkName = true; }
                if (kw.IndexOf("沐雲") > -1 && (kw.IndexOf("旅館") > -1 || kw.IndexOf("商旅") > -1)) { spKw = "沐雲頂國際商務旅館"; isSPMarkName = true; }
                if (kw.IndexOf("鶴茶樓") > -1 && kw.IndexOf("羅東") > -1) { spKw = "鶴茶樓-羅東成功店"; isSPMarkName = true; }
                if (kw.IndexOf("美術館") > -1 && kw.IndexOf("台南") > -1 && (kw.IndexOf("一館") > -1 || kw.IndexOf("1館") > -1 || kw.IndexOf("南門路") > -1)) { spKw = "台南市美術館-一館"; isSPMarkName = true; }
                if (kw.IndexOf("美術館") > -1 && kw.IndexOf("台南") > -1 && (kw.IndexOf("二館") > -1 || kw.IndexOf("2館") > -1 || kw.IndexOf("忠義路") > -1)) { spKw = "台南市美術館-二館"; isSPMarkName = true; }
                if (kw.IndexOf("科工館") > -1 && kw.IndexOf("南館") > -1) { spKw = "科學工藝博物館南館"; isSPMarkName = true; }
                if (kw.IndexOf("科工館") > -1 && kw.IndexOf("北館") > -1) { spKw = "科學工藝博物館北館"; isSPMarkName = true; }
                if (kw.IndexOf("竹東瑪露連") > -1) { spKw = "瑪露連竹東店"; isSPMarkName = true; }
                if (kw.IndexOf("中壢圖書館") > -1) { spKw = "桃園市立圖書館中壢分館"; isSPMarkName = true; }
                if (kw.IndexOf("大魯閣新時代店") > -1) { spKw = "大魯閣新時代購物中心"; isSPMarkName = true; }
                if (kw.IndexOf("無極紫勝宮") > -1) { spKw = "無極紫勝宮"; isSPMarkName = true; }
                if (kw.IndexOf("蘆洲功學社") > -1) { spKw = "功學社音樂廳"; isSPMarkName = true; }
                if (kw.IndexOf("恆勁科技") > -1) { spKw = "恆勁科技"; isSPMarkName = true; }
                if (kw.IndexOf("東森") > -1 && kw.IndexOf("寵物") > -1 && kw.IndexOf("澄清店") > -1) { spKw = "ETtoday寵物雲-台中澄清店"; isSPMarkName = true; }
                if (kw.IndexOf("龜記") > -1 && kw.IndexOf("中壢") > -1 && kw.IndexOf("中原店") > -1) { spKw = "龜記茗品-中壢中原店"; isSPMarkName = true; }
                if (kw.IndexOf("優品娃娃") > -1 && kw.IndexOf("台南") > -1 && kw.IndexOf("旗艦") > -1) { spKw = "優品娃娃屋-台南旗艦店"; isSPMarkName = true; }
                if (kw.IndexOf("龍巖") > -1 && (kw.IndexOf("會館") > -1 || kw.IndexOf("人本") > -1) && kw.IndexOf("台中") > -1 && kw.IndexOf("墓園") == -1) { spKw = "龍巖人本-台中會館"; isSPMarkName = true; }
                if (kw.IndexOf("龍巖") > -1 && kw.IndexOf("嘉雲會館") > -1) { spKw = "龍巖人本嘉雲寶塔"; isSPMarkName = true; }
                if (kw.IndexOf("關廟") > -1 && kw.IndexOf("納骨塔") > -1) { spKw = "關廟區第一納骨堂"; isSPMarkName = true; }
                if (kw.IndexOf("大內") > -1 && kw.IndexOf("納骨塔") > -1) { spKw = "大內區納骨塔"; isSPMarkName = true; }
                if (kw.IndexOf("永康") > -1 && kw.IndexOf("納骨塔") > -1) { spKw = "永康區第一公墓納骨塔"; isSPMarkName = true; }
                if (kw.IndexOf("柳營") > -1 && kw.IndexOf("納骨塔") > -1) { spKw = "柳營區第一公墓納骨塔"; isSPMarkName = true; }
                if (kw.IndexOf("中壢殯儀館") > -1 || kw.IndexOf("中壢區殯儀館") > -1) { spKw = "中壢區殯葬服務中心"; isSPMarkName = true; }
                if (kw.IndexOf("十鼓") > -1 && (kw.IndexOf("文化園區") > -1 || kw.IndexOf("文創園區") > -1)) { spKw = "十鼓仁糖文創園區"; isSPMarkName = true; }
                if (kw.IndexOf("秀泰") > -1 && (kw.IndexOf("百貨") > -1 || kw.IndexOf("生活廣場") > -1)) { spKw = "秀泰生活廣場-樹林店"; isSPMarkName = true; }
                if (kw.IndexOf("黎安酒店") > -1 || kw.IndexOf("黎安商旅") > -1) { spKw = "黎安商旅"; isSPMarkName = true; }
                if (kw.IndexOf("挪威森林") > -1 && kw.IndexOf("太平區") > -1) { spKw = "挪威森林-台中漫活館"; isSPMarkName = true; }
                if (kw.IndexOf("秋紅谷台中市") > -1 || kw.IndexOf("台中市秋紅谷") > -1) { spKw = "秋紅谷景觀生態公園"; isSPMarkName = true; }
                if (kw.IndexOf("小港社教館") > -1) { spKw = "高雄市立社教館"; isSPMarkName = true; }
                if (kw.IndexOf("萬芳三號公園") > -1) { spKw = "萬芳三號公園"; isSPMarkName = true; }
                if (kw.IndexOf("洲際") > -1 && (kw.IndexOf("前鎮") > -1 || kw.IndexOf("新光路") > -1) && (kw.IndexOf("酒店") > -1 || kw.IndexOf("飯店") > -1)) { spKw = "高雄洲際酒店"; isSPMarkName = true; }
                if (kw.IndexOf("朝陽科技大學") > -1 || kw.IndexOf("朝陽科大") > -1) { spKw = "朝陽科技大學"; isSPMarkName = true; }
                if (kw == "台北車站") { spKw = "台北火車站-請至捷運M3出口搭車"; isSPMarkName = true; }
                if (kw.IndexOf("高雄") > -1 && kw.IndexOf("國內機場") > -1) { spKw = "高雄機場-國內航廈"; isSPMarkName = true; }
                if (kw.IndexOf("台南機場") > -1) { spKw = "台南航空站"; isSPMarkName = true; }
                if (!isSPMarkName && kw.Length == 4 && kw.IndexOf("車站") > -1) { spKw = kw.Replace("車站", "火車站"); isSPMarkName = true; }
                if (kw == "板橋馥麗飯店" || kw == "馥麗商旅") { spKw = "馥俐商旅一館"; isSPMarkName = true; }
                if (kw == "台中林飯店") { spKw = "台中林酒店"; isSPMarkName = true; }
                if (kw == "台南市成功路富信大飯店") { spKw = "台南富信大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("呆獅") > -1 && kw.IndexOf("嘉義") > -1 && kw.IndexOf("雞肉") > -1) { spKw = "呆獅雞肉飯"; isSPMarkName = true; }
                if (kw == "國聯飯店") { spKw = "國聯大飯店"; isSPMarkName = true; }
                if (kw == "新竹國賓飯店") { spKw = "新竹國賓大飯店"; isSPMarkName = true; }
                if (kw == "中壢市古華大飯店" || kw == "古華飯店") { spKw = "古華花園飯店"; isSPMarkName = true; }
                if (kw == "麗寶賽車飯店") { spKw = "麗寶賽車主題旅店"; isSPMarkName = true; }
                if (kw.IndexOf("竹北喜來登") > -1 || kw.IndexOf("新竹喜來登") > -1 || kw.IndexOf("新竹豐邑喜來登") > -1) { spKw = "豐邑喜來登大飯店"; isSPMarkName = true; }
                if (kw == "左營聯上飯店") { spKw = "聯上大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("愛麗絲") > -1 && kw.IndexOf("飯店") > -1) { spKw = "愛麗絲國際大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("台南市立火葬場") > -1) { spKw = "台南市殯葬管理所南區火化場"; isSPMarkName = true; }
                if (kw.IndexOf("豐原醫院") > -1 || kw.IndexOf("省立豐原醫院") > -1) { spKw = "豐原醫院"; isSPMarkName = true; }
                if (kw.IndexOf("桃園醫院") > -1 || kw.IndexOf("省立桃園醫院") > -1 || kw.IndexOf("桃園署立醫院") > -1) { spKw = "桃園醫院"; isSPMarkName = true; }
                if (kw == "桃園榮民醫院" || kw == "桃園榮總") { spKw = "台北榮民總醫院桃園分院"; isSPMarkName = true; }
                if (kw == "中壢健保局") { spKw = "健保署北區業務組"; isSPMarkName = true; }
                if (kw.IndexOf("宜蘭金六結營區") > -1) { spKw = "金六結新訓中心"; isSPMarkName = true; }
                if (kw.IndexOf("高師大") > -1) { spKw = "高雄師範大學和平校區"; isSPMarkName = true; }
                if (kw.IndexOf("南港漢來酒店") > -1) { spKw = "台北漢來大飯店"; isSPMarkName = true; }
                if (kw.IndexOf("士林法院") > -1 && kw.IndexOf("內湖民事") > -1 && kw.IndexOf("大樓") > -1) { spKw = "內湖民事辦公大樓"; isSPMarkName = true; }
                if (kw.IndexOf("沙鹿童綜合") > -1 || kw.IndexOf("沙鹿童醫院") > -1) { spKw = "童綜合醫院沙鹿院區"; isSPMarkName = true; }
                if (kw.IndexOf("梧棲童綜合") > -1 || kw.IndexOf("梧棲童醫院") > -1) { spKw = "童綜合醫院梧棲院區"; isSPMarkName = true; }
                if (kw.IndexOf("擎天崗") > -1 && kw.IndexOf("遊客中心") > -1) { spKw = "擎天崗遊客服務站"; isSPMarkName = true; }
                if (kw.IndexOf("台中榮總") > -1) { spKw = "台中榮民總醫院"; isSPMarkName = true; }
                if (kw.IndexOf("三重同學匯") > -1) { spKw = "同學匯KTV-三重店"; isSPMarkName = true; }
                if (kw.IndexOf("板橋同學匯") > -1) { spKw = "同學匯KTV-板橋店"; isSPMarkName = true; }
                if (kw.IndexOf("林口同學匯") > -1) { spKw = "同學匯KTV-林口店"; isSPMarkName = true; }
                if (kw.IndexOf("台北國家音樂廳") > -1) { spKw = "兩廳院國家音樂廳"; isSPMarkName = true; }
                if (kw.IndexOf("署立南投醫院") > -1 || kw == "南投醫院") { spKw = "南投醫院"; isSPMarkName = true; }
                if (kw.IndexOf("旱溪夜市") > -1) { spKw = "旱溪夜市"; isSPMarkName = true; }
                if (kw == "內湖三總") { spKw = "三軍總醫院內湖院區"; isSPMarkName = true; }
                if (kw.IndexOf("北投三總") > -1 || kw.IndexOf("818醫院") > -1) { spKw = "三軍總醫院北投分院"; isSPMarkName = true; }
                if (kw.IndexOf("802醫院") > -1) { spKw = "國軍高雄總醫院"; isSPMarkName = true; }
                if (kw.IndexOf("花蓮機場") > -1) { spKw = "花蓮航空站"; isSPMarkName = true; }
                if (kw.IndexOf("和信醫院") > -1) { spKw = "和信治癌中心醫院"; isSPMarkName = true; }
                if (kw.IndexOf("書田醫院") > -1 || kw.IndexOf("書田診所") > -1) { spKw = "書田泌尿科眼科診所"; isSPMarkName = true; }
                if (kw.IndexOf("榮總三門診") > -1) { spKw = "台北榮總第三門診"; isSPMarkName = true; }
                if (kw.IndexOf("苗栗醫院") > -1 || kw.IndexOf("苗栗署立醫院") > -1) { spKw = "苗栗醫院"; isSPMarkName = true; }
                if (kw.IndexOf("巨城SOGO") > -1) { spKw = "新竹SOGO-巨城店"; isSPMarkName = true; }
                if (kw.IndexOf("冷水坑") > -1 && (kw.IndexOf("服務區") > -1 || kw.IndexOf("服務站") > -1)) { spKw = "冷水坑遊客服務站"; isSPMarkName = true; }
                if (kw.IndexOf("振興醫院") > -1 && kw.IndexOf("第一") > -1) { spKw = "振興醫院-第一醫療大樓"; isSPMarkName = true; }
                if (kw.IndexOf("振興醫院") > -1 && kw.IndexOf("第二") > -1) { spKw = "振興醫院-第二醫療大樓"; isSPMarkName = true; }
                if (kw.IndexOf("振興醫院") > -1 && kw.IndexOf("急診") > -1) { spKw = "振興醫院-急診部"; isSPMarkName = true; }
                if (kw.IndexOf("振興醫院") > -1 && kw.IndexOf("三院") > -1) { spKw = "振興醫院-三院區"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("振興醫院") > -1) { spKw = "振興醫院"; isSPMarkName = true; }
                if (kw.IndexOf("WHOTEL") > -1 || kw.IndexOf("W HOTEL") > -1) { spKw = "W-Hotel"; isSPMarkName = true; }
                if (kw.IndexOf("台灣大哥大") > -1 && kw.IndexOf("友愛店") > -1) { spKw = "台灣大哥大-嘉義友愛直營服務中心"; isSPMarkName = true; }
                if (kw.IndexOf("奇美醫院") == -1 && kw.IndexOf("奇美") > -1 && kw.IndexOf("醫院") > -1)
                {
                    if (kw.IndexOf("佳里") > -1) { spKw = "佳里奇美醫院"; isSPMarkName = true; }
                    if (kw.IndexOf("第一") > -1 || kw.IndexOf("中華路") > -1) { spKw = "奇美醫院第一醫療大樓"; isSPMarkName = true; }
                    if (kw.IndexOf("第三") > -1 || kw.IndexOf("甲頂路") > -1) { spKw = "奇美醫院第三醫療大樓"; isSPMarkName = true; }
                    if (kw.IndexOf("台南分院") > -1 || kw.IndexOf("樹林") > -1) { spKw = "奇美醫院台南分院"; isSPMarkName = true; }
                    if (kw.IndexOf("柳營") > -1 || kw.IndexOf("太康") > -1) { spKw = "柳營奇美醫院"; isSPMarkName = true; }
                    if (!isSPMarkName) { spKw = "奇美醫院"; }
                }
                if (kw.IndexOf("中莊國小") > -1 || kw.IndexOf("中庄國小") > -1) { spKw = "中庄國小"; isSPMarkName = true; }
                if (kw.IndexOf("潔林牙科") > -1 || kw.IndexOf("潔林牙醫") > -1) { spKw = "潔林牙醫診所"; isSPMarkName = true; }
                if (kw.IndexOf("肯德基") > -1 && kw.IndexOf("鳳山") > -1 && kw.IndexOf("五甲") > -1) { spKw = "肯德基-鳳山五甲一餐廳"; isSPMarkName = true; }
                if (kw.IndexOf("環球") > -1 && kw.IndexOf("屏東") > -1 && (kw.IndexOf("百貨") > -1 || kw.IndexOf("購物") > -1)) { spKw = "環球購物中心-屏東店"; isSPMarkName = true; }
                if (kw.IndexOf("奇異果") > -1 && kw.IndexOf("中壢") > -1 && (kw.IndexOf("旅店") > -1 || kw.IndexOf("旅館") > -1)) { spKw = "奇異果旅店-中壢車站店"; isSPMarkName = true; }
                if (kw.IndexOf("翠屏") > -1 && (kw.IndexOf("國中") > -1 || kw.IndexOf("中小學") > -1)) { spKw = "翠屏國中"; isSPMarkName = true; }
                if (kw.IndexOf("桃園巨蛋") > -1) { spKw = "桃園巨蛋體育館"; isSPMarkName = true; }
                if (kw.IndexOf("家樂福") > -1 && kw.IndexOf("內湖") > -1 && kw.IndexOf("店") > -1) { spKw = "家樂福-內湖店"; isSPMarkName = true; }
                if (kw.IndexOf("桃園市監理站") > -1) { spKw = "桃園監理站"; isSPMarkName = true; }
                if (kw.IndexOf("台中五權車站") > -1) { spKw = "五權車站"; isSPMarkName = true; }
                if (kw.IndexOf("國立清華大學") > -1) { spKw = "清華大學"; isSPMarkName = true; }
                if (kw.IndexOf("親子夢想館") > -1 && kw.IndexOf("竹北") > -1) { spKw = "新竹縣親子夢想館-"; isSPMarkName = true; }
                if (kw.IndexOf("彰基") > -1 && kw.IndexOf("兒童") > -1 && kw.IndexOf("醫院") > -1) { spKw = "彰化基督教醫院兒童醫院"; isSPMarkName = true; }
                if (kw.IndexOf("龍邦世貿") > -1 && kw.IndexOf("A棟") > -1) { spKw = "龍邦世貿大樓A棟"; isSPMarkName = true; }
                if (kw.IndexOf("藍天樓") > -1 && kw.IndexOf("空軍") > -1) { spKw = "空軍藍天樓"; isSPMarkName = true; }
                if (kw.IndexOf("四林國小") > -1 || kw.IndexOf("泗林國小") > -1) { spKw = "四林國小"; isSPMarkName = true; }
                if (kw.IndexOf("ELEVEN") > -1 && (kw.IndexOf("口莊門市") > -1 || kw.IndexOf("口庄門市") > -1 || kw.IndexOf("口庄店") > -1 || kw.IndexOf("口莊店") > -1)) { spKw = "7－ELEVEN-口庄門市"; isSPMarkName = true; }
                if (kw.IndexOf("ELEVEN") > -1 && (kw.IndexOf("豐村店") > -1 || kw.IndexOf("豐村門市") > -1)) { spKw = "7－ELEVEN-豐村門市"; isSPMarkName = true; }
                if (kw.IndexOf("挪威森林") > -1 && kw.IndexOf("台中") > -1 && kw.IndexOf("漫活") > -1) { spKw = "挪威森林-台中漫活館"; isSPMarkName = true; }
                if (kw.IndexOf("新莊火車站") > -1 || kw.IndexOf("新莊車站") > -1) { spKw = "新莊火車站"; isSPMarkName = true; }
                if (kw.IndexOf("五權火車站") > -1 || kw.IndexOf("五權車站") > -1) { spKw = "五權火車站"; isSPMarkName = true; }
                if (kw.IndexOf("成功火車站") > -1 || kw.IndexOf("成功車站") > -1) { spKw = "成功火車站"; isSPMarkName = true; }
                if (kw.IndexOf("竹南火車站") > -1 || kw.IndexOf("竹南車站") > -1) { spKw = "竹南火車站"; isSPMarkName = true; }
                if (kw.IndexOf("七堵火車站") > -1 || kw.IndexOf("七堵車站") > -1) { spKw = "七堵火車站"; isSPMarkName = true; }
                if (kw.IndexOf("台北醫學大學") > -1 && kw.IndexOf("急診") > -1) { spKw = "北醫急診室"; isSPMarkName = true; }
                if (kw.IndexOf("門諾醫院") > -1 && kw.IndexOf("壽豐") > -1) { spKw = "門諾醫院壽豐分院"; isSPMarkName = true; }
                if (kw.IndexOf("門諾醫院") > -1 && kw.IndexOf("信實") > -1) { spKw = "門諾醫院信實樓"; isSPMarkName = true; }
                if (!isSPMarkName && kw.IndexOf("門諾醫院") > -1) { spKw = "門諾醫院"; isSPMarkName = true; }
                if (kw.IndexOf("聖馬爾定") > -1 && kw.IndexOf("民權") > -1 && (kw.IndexOf("分院") > -1 || kw.IndexOf("醫院") > -1)) { spKw = "聖馬爾定醫院民權院區"; isSPMarkName = true; }
                if (kw.IndexOf("台北啤酒廠") > -1) { spKw = "台北啤酒工場"; isSPMarkName = true; }
                if (kw.IndexOf("逢甲十八魂") > -1) { spKw = "十八魂手串燒烤"; isSPMarkName = true; }
                if (kw.IndexOf("老虎城") > -1 && kw.IndexOf("台中") > -1) { spKw = "老虎城購物中心"; isSPMarkName = true; }
                if (kw.IndexOf("台南大學") > -1 && kw.IndexOf("榮譽") > -1) { spKw = "台南大學榮譽教學中心"; isSPMarkName = true; }
                if ((kw.IndexOf("禦宿") > -1 || kw.IndexOf("御宿") > -1) && (kw.IndexOf("商旅") > -1 || kw.IndexOf("旅館") > -1))
                {
                    if (kw.IndexOf("明華") > -1) { spKw = "御宿商旅-明華館"; }
                    if (kw.IndexOf("中山") > -1) { spKw = "御宿商旅-中山館"; }
                    if (kw.IndexOf("中央公園") > -1) { spKw = "御宿商旅-中央公園館"; }
                    if (kw.IndexOf("後驛") > -1) { spKw = "御宿商旅-後驛館"; }
                    if (kw.IndexOf("站前") > -1) { spKw = "御宿商旅-站前一館"; }
                    if (kw.IndexOf("博愛") > -1) { spKw = "御宿商旅-博愛館"; }
                    if (kw.IndexOf("雄中") > -1) { spKw = "御宿商旅雄中館"; }
                    isSPMarkName = true;
                }
                if (kw.IndexOf("親親戲院") > -1 || kw.IndexOf("親親影城") > -1) { spKw = "親親戲院"; isSPMarkName = true; }
                if (kw.IndexOf("茶六") > -1 && kw.IndexOf("朝富") > -1) { spKw = "茶六燒肉堂-台中朝富店"; isSPMarkName = true; }
                if (kw.IndexOf("茶六") > -1 && kw.IndexOf("公益") > -1) { spKw = "茶六燒肉堂-公益店"; isSPMarkName = true; }
                if (kw.IndexOf("茶六") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("左營") > -1)) { spKw = "茶六燒肉堂"; isSPMarkName = true; }
                if (kw.IndexOf("尚美") > -1 && kw.IndexOf("高雄") > -1 && (kw.IndexOf("KTV") > -1 || kw.IndexOf("保齡球") > -1)) { spKw = "尚美保齡球館"; isSPMarkName = true; }
                if (kw.IndexOf("聖功醫院") > -1 && kw.IndexOf("輕軌") == -1) { spKw = "聖功醫院"; isSPMarkName = true; }
                if (kw.IndexOf("麥當勞") > -1 && kw.IndexOf("嘉義") > -1 && kw.IndexOf("垂楊") > -1) { spKw = "麥當勞-嘉義垂楊門市"; isSPMarkName = true; }
                if (kw.IndexOf("真慶宮") > -1) { spKw = "真慶宮"; isSPMarkName = true; }
                if (kw.IndexOf("寶雅") > -1 && (kw.IndexOf("台南") > -1 || kw.IndexOf("安南") > -1) && (kw.IndexOf("安和店") > -1 || kw.IndexOf("安和門市") > -1)) { spKw = "寶雅-台南安和店"; isSPMarkName = true; }
                if (kw.IndexOf("托斯卡尼尼") > -1 && (kw.IndexOf("平鎮") > -1 || kw.IndexOf("中壢") > -1)) { spKw = "托斯卡尼尼-中壢店"; isSPMarkName = true; }
                if (kw.IndexOf("虎尾") > -1 && kw.IndexOf("郵局") > -1 && (kw.IndexOf("科大") > -1 || kw.IndexOf("大學") > -1)) { spKw = "虎尾科技大學郵局"; isSPMarkName = true; }
                if (kw.IndexOf("卡爾登飯店") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("北大路") > -1) && kw.IndexOf("中華") == -1) { spKw = "卡爾登飯店-新竹館"; isSPMarkName = true; }
                if (kw.IndexOf("卡爾登飯店") > -1 && (kw.IndexOf("中華館") > -1 || kw.IndexOf("中華路") > -1)) { spKw = "卡爾登飯店-中華館"; isSPMarkName = true; }
                if (kw.IndexOf("卡爾登飯店") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("忠明南路") > -1)) { spKw = "卡爾登飯店-台中館"; isSPMarkName = true; }

                #region 多查詢條件
                if (!isSPMarkName)
                {
                    // 且
                    if (kw.IndexOf("龍德廟") > -1 && (kw.IndexOf("南投") > -1 || kw.IndexOf("草屯") > -1)) { spKw = "南投縣草屯鎮&&龍德廟"; isSPMarkName = true; }
                    if (kw.IndexOf("龍德廟") > -1 && (kw.IndexOf("宜蘭") > -1 || kw.IndexOf("蘇澳") > -1)) { spKw = "宜蘭縣蘇澳鎮&&龍德廟"; isSPMarkName = true; }
                    if (kw.IndexOf("樂天宮") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("豐原") > -1)) { spKw = "台中市豐原區&&樂天宮"; isSPMarkName = true; }
                    if (kw.IndexOf("揚昇") > -1 && (kw.IndexOf("診所") > -1 || kw.IndexOf("樹林") > -1) && kw.IndexOf("醫美") == -1) { spKw = "揚昇診所&&新北市樹林區"; isSPMarkName = true; }
                    if (!isSPMarkName && kw.IndexOf("揚昇") > -1 && (kw.IndexOf("醫美") > -1 || kw.IndexOf("診所") > -1 || kw.IndexOf("台中") > -1 || kw.IndexOf("西屯") > -1)) { spKw = "揚昇醫美診所"; isSPMarkName = true; }
                    if (kw.IndexOf("消防局") > -1 && kw.IndexOf("安和") > -1 && (kw.IndexOf("台南") > -1 || kw.IndexOf("安南") > -1)) { spKw = "消防局安和分隊&&台南市安南區"; isSPMarkName = true; }
                    if (kw.IndexOf("消防局") > -1 && kw.IndexOf("安和") > -1 && (kw.IndexOf("新店") > -1 || kw.IndexOf("新北") > -1)) { spKw = "消防局安和分隊&&新北市新店區"; isSPMarkName = true; }
                    if (!isSPMarkName && kw.IndexOf("消防局") > -1 && kw.IndexOf("安和") > -1) { spKw = "消防局安和分隊&&台北市大安區"; isSPMarkName = true; }
                    if (kw.IndexOf("高雄展覽館") > -1 && kw.IndexOf("輕軌") == -1 && kw.IndexOf("店") == -1 && kw.IndexOf("站") == -1 && kw.IndexOf("捷運") == -1) { spKw = "高雄展覽館&&排班處"; isSPMarkName = true; }
                    if (kw.IndexOf("台中歌劇院") > -1) { spKw = "台中歌劇院&&排班處"; isSPMarkName = true; }
                    if (kw.IndexOf("ATT") > -1 && kw.IndexOf("FUN") > -1 && kw.IndexOf("松智路") > -1) { spKw = "台北市信義區松壽路12號&&松智路側門口"; isSPMarkName = true; }
                    if (kw.IndexOf("市政南二路") > -1 && kw.IndexOf("台中") > -1 && kw.IndexOf("西屯") == -1 && kw.IndexOf("南屯") == -1)
                    {
                        var temp = kw.Replace("台中市", "").Replace("西屯區", "").Replace("南屯區", "").Replace("台中", "").Replace("西屯", "").Replace("南屯", "");
                        spKw = "台中市&&" + temp;
                        isSPMarkName = true;
                    }
                    if (kw.IndexOf("光復") > -1 && (kw.IndexOf("國小") > -1 || kw.IndexOf("小學") > -1))
                    {
                        if (kw.IndexOf("中和") > -1 || kw.IndexOf("新北") > -1) { spKw = "新北市中和區&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("台北") > -1 || kw.IndexOf("信義") > -1) { spKw = "台北市信義區&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("花蓮") > -1 || kw.IndexOf("光復鄉") > -1) { spKw = "花蓮縣光復鄉&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("雲林") > -1 || kw.IndexOf("虎尾") > -1) { spKw = "雲林縣虎尾鎮&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("宜蘭") > -1) { spKw = "宜蘭縣宜蘭市&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("南投") > -1) { spKw = "南投縣南投市&&光復國小"; isSPMarkName = true; }
                        if (kw.IndexOf("霧峰") > -1 || kw.IndexOf("柳豐路") > -1) { spKw = "台中市霧峰區柳豐路&&光復國小"; isSPMarkName = true; }
                        if (!isSPMarkName && (kw.IndexOf("台中") > -1 || kw.IndexOf("中區") > -1 || kw.IndexOf("三民路") > -1)) { spKw = "台中市中區三民路&&光復國小"; isSPMarkName = true; }
                    }
                    if (kw.IndexOf("瑞芳") > -1 && (kw.IndexOf("禦花園") > -1 || kw.IndexOf("御花園") > -1) && kw.IndexOf("社區") > -1) { spKw = "新北市瑞芳區&&御花園"; isSPMarkName = true; }
                    if (kw == "帝后飯店") { spKw = "高雄市鹽埕區&&帝后大飯店"; isSPMarkName = true; }
                    if (kw == "高雄漢來飯店") { spKw = "高雄市前金區成功一路&&漢來大飯店"; isSPMarkName = true; }
                    if (kw.IndexOf("龍華") > -1 && kw.IndexOf("屏東") > -1 && (kw.IndexOf("幼稚園") > -1 || kw.IndexOf("幼兒園") > -1)) { spKw = "屏東縣屏東市&&龍華幼兒園"; isSPMarkName = true; }
                    if (!isSPMarkName && kw.IndexOf("龍華") > -1 && (kw.IndexOf("幼稚園") > -1 || kw.IndexOf("幼兒園") > -1)) { spKw = "台北市南港區&&龍華幼兒園"; isSPMarkName = true; }
                    if ((kw.IndexOf("成都川菜館") > -1 || kw.IndexOf("成都餐廳") > -1) && kw.IndexOf("沙鹿") > -1) { spKw = "台中市沙鹿區&&成都川菜館"; isSPMarkName = true; }
                    if (!isSPMarkName && (kw.IndexOf("成都川菜館") > -1 || kw.IndexOf("成都餐廳") > -1) && kw.IndexOf("萬華") > -1) { spKw = "台北市萬華區&&成都川菜館"; isSPMarkName = true; }
                    if (!isSPMarkName && (kw.IndexOf("成都川菜館") > -1 || kw.IndexOf("成都餐廳") > -1)) { spKw = "台北市大安區&&成都川菜館"; isSPMarkName = true; }

                    // 或
                    if (kw.IndexOf("眼科") > -1 && kw.IndexOf("大學眼科") == -1)
                    {
                        spKw = spKw.Replace("醫院", "").Replace("診所", "").Replace("眼科", "");
                        spKw2 = "眼科||大學眼科"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("高級中學") > -1 && kw.IndexOf("高中") == -1)
                    {
                        spKw = spKw.Replace("高級中學", "").Replace("高中", "");
                        spKw2 = "高級中學||高中"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("中學") > -1 && kw.IndexOf("高級中學") == -1 && kw.IndexOf("高中") == -1)
                    {
                        spKw = spKw.Replace("中學", "").Replace("高中", "");
                        spKw2 = "中學||高中"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("監理站") > -1 || kw.IndexOf("監理所") > -1)
                    {
                        spKw = spKw.Replace("監理站", "").Replace("監理所", "");
                        spKw2 = "監理站||監理所"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("車站") > -1 && kw.IndexOf("火車站") == -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("車站後街") == -1)
                    {
                        spKw = spKw.Replace("車站", "").Replace("火車站", "");
                        spKw2 = "車站||火車站"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("警察局") > -1 || kw.IndexOf("警局") > -1)
                    {
                        spKw = spKw.Replace("警察局", "").Replace("警局", "");
                        spKw2 = "警察局||警局"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("火葬場") > -1 || kw.IndexOf("火化場") > -1)
                    {
                        spKw = spKw.Replace("火葬場", "").Replace("火化場", "");
                        spKw2 = "火葬場||火化場"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("寵物醫院") > -1 || kw.IndexOf("動物醫院") > -1)
                    {
                        spKw = spKw.Replace("寵物醫院", "").Replace("動物醫院", "");
                        spKw2 = "寵物醫院||動物醫院"; isSPMarkName = true;
                    }
                    if (kw.IndexOf("納骨塔") > -1 || kw.IndexOf("納骨堂") > -1 || kw.IndexOf("靈骨塔") > -1)
                    {
                        spKw = spKw.Replace("靈骨塔", "").Replace("納骨塔", "").Replace("納骨堂", "");
                        spKw2 = "靈骨塔||納骨塔||納骨堂"; isSPMarkName = true;
                    }
                }
                #endregion

                if (isSPMarkName && !string.IsNullOrEmpty(spKw))
                {
                    // 移除指定贅字2
                    if (spKw.IndexOf("-") == -1)
                    {
                        foreach (var str in excessWords2) { spKw = spKw.Replace(str, ""); }
                    }
                    if (spKw.IndexOf("&&") > -1 && string.IsNullOrEmpty(spKw2))
                    {
                        var thesaurus = "";
                        foreach (var sp in spKw.Split("&&"))
                        {
                            if (!string.IsNullOrEmpty(sp))
                            {
                                thesaurus += " FORMSOF(THESAURUS," + sp + ") and";
                            }
                        }
                        thesaurus = thesaurus.Remove(thesaurus.Length - 3, 3).Trim();
                        var getMarkName = await GoASRAPI("", thesaurus).ConfigureAwait(false);
                        if (getMarkName != null)
                        {
                            newSpeechAddress.Lng_X = getMarkName.Lng;
                            newSpeechAddress.Lat_Y = getMarkName.Lat;
                            ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    else if (!string.IsNullOrEmpty(spKw2))
                    {
                        var getAddrNoNum = SetGISAddress(new SearchGISAddress { Address = spKw, IsCrossRoads = false });
                        var thesaurus0 = "";
                        var thesaurus = "";
                        var thesaurusC = "";
                        if (!string.IsNullOrEmpty(getAddrNoNum.City))
                        {
                            spKw = spKw.Replace(getAddrNoNum.City, "");
                            thesaurusC = " FORMSOF(THESAURUS," + getAddrNoNum.City + ") and";
                            thesaurus0 += thesaurusC;
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Dist))
                        {
                            spKw = spKw.Replace(getAddrNoNum.Dist, "");
                            thesaurus0 += " FORMSOF(THESAURUS," + getAddrNoNum.Dist + ") and";
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Road))
                        {
                            spKw = spKw.Replace(getAddrNoNum.Road, "");
                            thesaurus0 += " FORMSOF(THESAURUS," + getAddrNoNum.Road + ") and";
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Sect))
                        {
                            spKw = spKw.Replace(getAddrNoNum.Sect, "");
                            thesaurus0 += " FORMSOF(THESAURUS," + getAddrNoNum.Sect + ") and";
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Lane)) { spKw = spKw.Replace(getAddrNoNum.Lane, ""); }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Non)) { spKw = spKw.Replace(getAddrNoNum.Non, ""); }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Num))
                        {
                            spKw = spKw.Replace(getAddrNoNum.Num, "");
                            thesaurus0 += " FORMSOF(THESAURUS," + getAddrNoNum.Num + ") and";
                        }
                        if (!string.IsNullOrEmpty(spKw) || !string.IsNullOrEmpty(getAddrNoNum.City) || !string.IsNullOrEmpty(getAddrNoNum.Dist))
                        {
                            if (!string.IsNullOrEmpty(spKw)) { thesaurus0 += " FORMSOF(THESAURUS," + spKw + ") and"; }
                            foreach (var sp in spKw2.Split("||"))
                            {
                                if (!string.IsNullOrEmpty(sp))
                                {
                                    thesaurus += " FORMSOF(THESAURUS," + sp + ") or";
                                }
                            }
                            thesaurus = thesaurus.Remove(thesaurus.Length - 2, 2).Trim();
                            var getMarkName = await GoASRAPI("", thesaurus0 + " (" + thesaurus + ")", markName: !string.IsNullOrEmpty(getAddrNoNum.City) ? getAddrNoNum.City : "").ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            if (!string.IsNullOrEmpty(thesaurusC))
                            {
                                getMarkName = await GoASRAPI("", thesaurus0.Replace(thesaurusC, "") + " (" + thesaurus + ")").ConfigureAwait(false);
                                if (getMarkName != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    else
                    {
                        var getMarkName = await GoASRAPI(spKw, "", markName: spKw).ConfigureAwait(false);
                        if (getMarkName != null)
                        {
                            newSpeechAddress.Lng_X = getMarkName.Lng;
                            newSpeechAddress.Lat_Y = getMarkName.Lat;
                            ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion
                // 移除樓
                var checkFloor = kw.IndexOf("樓");
                if (checkNum > -1 && checkFloor > -1 && checkFloor > checkNum)
                {
                    var arrKw = kw.Split("號");
                    kw = arrKw[0] + "號";
                    if (arrKw.Length > 1 && !string.IsNullOrEmpty(arrKw[1]))
                    {
                        // 檢查 地址單位
                        var getNewAddr1 = SetGISAddress(new SearchGISAddress { Address = arrKw[0] + "號", IsCrossRoads = false });
                        var getNewAddr2 = SetGISAddress(new SearchGISAddress { Address = arrKw[1][(arrKw[1].IndexOf("樓") + 1)..] + "號", IsCrossRoads = false });
                        if (string.IsNullOrEmpty(getNewAddr1.City) && !string.IsNullOrEmpty(getNewAddr2.City)) { getNewAddr1.City = getNewAddr2.City; }
                        if (string.IsNullOrEmpty(getNewAddr1.Dist) && !string.IsNullOrEmpty(getNewAddr2.Dist)) { getNewAddr1.Dist = getNewAddr2.Dist; }
                        if (string.IsNullOrEmpty(getNewAddr1.Road) && !string.IsNullOrEmpty(getNewAddr2.Road) && (getNewAddr2.Road.EndsWith("路") || getNewAddr2.Road.EndsWith("街") || getNewAddr2.Road.EndsWith("道")))
                        {
                            if (getNewAddr2.Road.Length <= 7 && getNewAddr2.Road.IndexOf("出口區") == -1 && getNewAddr2.Road.IndexOf("園區") == -1 && getNewAddr2.Road.IndexOf("工業區") == -1 && getNewAddr2.Road.IndexOf("光復新村") == -1)
                            {
                                getNewAddr1.Road = getNewAddr2.Road;
                            }
                        }
                        else if (!string.IsNullOrEmpty(getNewAddr1.Road) && !string.IsNullOrEmpty(getNewAddr2.Road) && getNewAddr1.Road.Length != getNewAddr2.Road.Length &&
                            (getNewAddr2.Road.EndsWith("路") || getNewAddr2.Road.EndsWith("街") || getNewAddr2.Road.EndsWith("道")))
                        {
                            if (getNewAddr2.Road.Length <= 7 && getNewAddr2.Road.IndexOf("出口區") == -1 && getNewAddr2.Road.IndexOf("園區") == -1 && getNewAddr2.Road.IndexOf("工業區") == -1 && getNewAddr2.Road.IndexOf("光復新村") == -1)
                            {
                                getNewAddr1.Road = getNewAddr2.Road;
                            }
                        }
                        if (string.IsNullOrEmpty(getNewAddr1.Sect) && !string.IsNullOrEmpty(getNewAddr2.Sect)) { getNewAddr1.Sect = getNewAddr2.Sect; }
                        if (!string.IsNullOrEmpty(getNewAddr1.Lane) && getNewAddr1.Lane.StartsWith("-")) { getNewAddr1.Lane = ""; }
                        if (!string.IsNullOrEmpty(getNewAddr2.Lane) && getNewAddr2.Lane.StartsWith("-")) { getNewAddr2.Lane = ""; }
                        if (string.IsNullOrEmpty(getNewAddr1.Lane) && !string.IsNullOrEmpty(getNewAddr2.Lane)) { getNewAddr1.Lane = getNewAddr2.Lane; }
                        if (string.IsNullOrEmpty(getNewAddr1.Non) && !string.IsNullOrEmpty(getNewAddr2.Non)) { getNewAddr1.Non = getNewAddr2.Non; }
                        if (!string.IsNullOrEmpty(getNewAddr1.Num))
                        {
                            var tempNum = getNewAddr1.Num.Replace("7之ELEVEN", "");
                            var pattern = @"[\u4e00-\u9fa5]";
                            tempNum = Regex.Replace(tempNum, pattern, "");
                            if (string.IsNullOrEmpty(tempNum)) { getNewAddr1.Num = ""; }
                        }
                        if (!string.IsNullOrEmpty(getNewAddr2.Num))
                        {
                            var tempNum = getNewAddr2.Num.Replace("7之ELEVEN", "");
                            var pattern = @"[\u4e00-\u9fa5]";
                            tempNum = Regex.Replace(tempNum, pattern, "");
                            if (string.IsNullOrEmpty(tempNum)) { getNewAddr2.Num = ""; }
                        }
                        if (string.IsNullOrEmpty(getNewAddr1.Num) && !string.IsNullOrEmpty(getNewAddr2.Num)) { getNewAddr1.Num = getNewAddr2.Num; }
                        kw = getNewAddr1.City + getNewAddr1.Dist + getNewAddr1.Road + getNewAddr1.Sect + getNewAddr1.Lane + getNewAddr1.Non + getNewAddr1.Num;
                        if (!string.IsNullOrEmpty(getNewAddr1.Num) && !string.IsNullOrEmpty(getNewAddr2.Num) && getNewAddr1.Num != getNewAddr2.Num)
                        {
                            // 兩個號 先用後面的號找 再用前面的號找
                            var _kw = kw.Replace(getNewAddr1.Num, getNewAddr2.Num);
                            var getTowNum = await GoASRAPI(_kw, "", markName: _kw).ConfigureAwait(false);
                            if (getTowNum != null)
                            {
                                newSpeechAddress.Lng_X = getTowNum.Lng;
                                newSpeechAddress.Lat_Y = getTowNum.Lat;
                                ShowAddr(0, getTowNum.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            getTowNum = await GoASRAPI(kw, "", markName: kw).ConfigureAwait(false);
                            if (getTowNum != null)
                            {
                                newSpeechAddress.Lng_X = getTowNum.Lng;
                                newSpeechAddress.Lat_Y = getTowNum.Lat;
                                ShowAddr(0, getTowNum.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                }
                // 如果有多個號，取後面那組(無之號的情況下)
                var holdNum = ""; // 保留被移除的號
                if (checkNum > -1 && kw.IndexOf("捷運") == -1)
                {
                    var arrN = kw.Split("號");
                    if (arrN.Length > 2 && (kw.IndexOf("要到") > -1 || kw.IndexOf("要去") > -1 || kw.IndexOf("去到") > -1 || kw.IndexOf("對面") > -1))
                    {
                        // 直接移除後面的
                        kw = arrN[0] + "號";
                        arrN = kw.Split("號");
                    }
                    if (arrN.Length > 2 && kw.IndexOf("之") == -1)
                    {
                        var tempNum = arrN[^2];
                        var isNext = false;
                        if (tempNum.Length > 6)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = tempNum + "號", IsCrossRoads = false });
                            if (string.IsNullOrEmpty(getNewAddr.City) && string.IsNullOrEmpty(getNewAddr.Dist) && !string.IsNullOrEmpty(getNewAddr.Road))
                            {
                                // 檢查路名裡面是否有 City/Dist
                                foreach (var c in allCity.Concat(allCityCounty))
                                {
                                    if (getNewAddr.Road == (c + "路") || getNewAddr.Road == (c + "街"))
                                    {
                                        break;
                                    }
                                    if (getNewAddr.Road.IndexOf(c) > -1)
                                    {
                                        var c1 = getNewAddr.Road.IndexOf(c + "市");
                                        var c2 = getNewAddr.Road.IndexOf(c + "縣");
                                        if (c1 > -1)
                                        {
                                            getNewAddr.Road = getNewAddr.Road[c1..];
                                            break;
                                        }
                                        if (c2 > -1)
                                        {
                                            getNewAddr.Road = getNewAddr.Road[c2..];
                                            break;
                                        }
                                    }
                                }
                                var _getNewAddr = SetGISAddress(new SearchGISAddress { Address = getNewAddr.Road, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_getNewAddr.City)) { getNewAddr.City = _getNewAddr.City; }
                                if (!string.IsNullOrEmpty(_getNewAddr.Dist)) { getNewAddr.Dist = _getNewAddr.Dist; }
                                if (!string.IsNullOrEmpty(_getNewAddr.Road)) { getNewAddr.Road = _getNewAddr.Road; }
                                if (!string.IsNullOrEmpty(_getNewAddr.Sect)) { getNewAddr.Sect = _getNewAddr.Sect; }
                                if (!string.IsNullOrEmpty(_getNewAddr.Lane)) { getNewAddr.Lane = _getNewAddr.Lane; }
                                if (!string.IsNullOrEmpty(_getNewAddr.Non)) { getNewAddr.Non = _getNewAddr.Non; }
                            }

                            if ((!string.IsNullOrEmpty(getNewAddr.City) || !string.IsNullOrEmpty(getNewAddr.Dist)) && !string.IsNullOrEmpty(getNewAddr.Num) && (!string.IsNullOrEmpty(getNewAddr.Road) || !string.IsNullOrEmpty(getNewAddr.Lane)))
                            {
                                // 如果有完整行政單位直接取代
                                kw = getNewAddr.City + getNewAddr.Dist + getNewAddr.Road + getNewAddr.Sect + getNewAddr.Lane + getNewAddr.Non + getNewAddr.Num;
                                isNext = true;
                            }
                        }
                        if (!isNext)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                            var newKw = "";
                            if (!string.IsNullOrEmpty(getNewAddr.City)) { newKw += getNewAddr.City; }
                            if (!string.IsNullOrEmpty(getNewAddr.Dist)) { newKw += getNewAddr.Dist; }
                            if (string.IsNullOrEmpty(getNewAddr.Road))
                            {
                                // 檢查路被放到號裡
                                foreach (var other in chineseNum)
                                {
                                    var arrKw = getNewAddr.Num.Split(other);
                                    if (arrKw.Length > 1 && !string.IsNullOrEmpty(arrKw[0]))
                                    {
                                        var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = other, IsCrossRoads = false });
                                        getNewAddr.Num = getNewAddr.Num.Replace(other, getOtherAddr.Num);
                                        break;
                                    }
                                }
                                var _newAddr = SetGISAddress(new SearchGISAddress { Address = getNewAddr.Num, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_newAddr.Road)) { getNewAddr.Road = _newAddr.Road; };
                            }
                            if (!string.IsNullOrEmpty(getNewAddr.Road)) { newKw += getNewAddr.Road; }
                            if (!string.IsNullOrEmpty(getNewAddr.Sect)) { newKw += getNewAddr.Sect; }
                            if (!string.IsNullOrEmpty(getNewAddr.Lane))
                            {
                                if (getNewAddr.Lane.IndexOf("號") > -1 && getNewAddr.Lane.IndexOf("號") < getNewAddr.Lane.IndexOf("巷"))
                                {
                                    var arrL = getNewAddr.Lane.Split("號");
                                    getNewAddr.Lane = arrL[1];
                                }
                                newKw += getNewAddr.Lane;
                            }
                            if (!string.IsNullOrEmpty(getNewAddr.Non)) { newKw += getNewAddr.Non; }

                            if (tempNum.IndexOf("不對4") > -1 && tempNum.IndexOf("41") > -1)
                            {
                                tempNum = "11";
                            }
                            if (tempNum.IndexOf("對") > -1 && tempNum.IndexOf("之") == -1)
                            {
                                var tempNum1 = arrN[^2];
                                tempNum1 = Regex.Replace(tempNum1, @"\d", "");
                                if (tempNum.IndexOf("不對4") > -1 && tempNum.IndexOf("41") > -1)
                                {
                                    tempNum = "11";
                                }
                                else
                                {
                                    foreach (var c in tempNum1)
                                    {
                                        var _c = c.ToString();
                                        if (_c != "臨" || _c != "附")
                                        {
                                            tempNum = tempNum.Replace(_c, "");
                                        }
                                    }
                                }
                            }
                            // 檢查號
                            if (!string.IsNullOrEmpty(tempNum))
                            {
                                if (tempNum.IndexOf("樓") > -1 && tempNum.IndexOf("樓") < (tempNum.Length - 1))
                                {
                                    tempNum = tempNum.Substring(tempNum.IndexOf("樓") + 1, tempNum.Length - tempNum.IndexOf("樓") - 1);
                                }
                                var tempNumAddr = SetGISAddress(new SearchGISAddress { Address = tempNum, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(tempNumAddr.Num))
                                {
                                    kw = newKw + tempNumAddr.Num;
                                    if (!string.IsNullOrEmpty(getNewAddr.Num) && tempNumAddr.Num != getNewAddr.Num)
                                    {
                                        holdNum = getNewAddr.Num;
                                    }
                                }
                            }
                        }
                    }
                    if (arrN.Length > 2 && kw.IndexOf("號之") > -1)
                    {
                        // 把"之"之前的號拿掉
                        var tempNum = arrN[^2];
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = tempNum.Replace("之", "") + "號", IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getNewAddr.Num))
                        {
                            kw = arrN[0] + "之" + getNewAddr.Num;
                        }
                        if (arrN.Length > 2 && !string.IsNullOrEmpty(arrN[^1]))
                        {
                            kw += arrN[^1];
                        }
                    }
                }
                // 第一個字是"XX"，移除
                if (kw.StartsWith("到")) { kw = kw[1..]; }
                if (kw.StartsWith("為為")) { kw = kw[2..]; }
                if (kw.StartsWith("為")) { kw = kw[1..]; }
                if (kw.StartsWith("是")) { kw = kw[1..]; }
                if (kw.StartsWith("魏")) { kw = kw[1..]; }
                checkNum = kw.IndexOf("號");
                if (checkNum == -1)
                {
                    // 移除關鍵字後面的贅字
                    foreach (var str in oneNumReplace)
                    {
                        var getW = str.Split("|")[0];
                        if (kw.IndexOf(getW) > -1)
                        {
                            kw = kw.Split(getW)[0] + getW;
                            break;
                        }
                    }
                }
                if (kw.IndexOf("口") == -1)
                {
                    string[] reW = { "街街", "段段", "巷巷", "弄弄", "號號" };
                    foreach (var str in reW)
                    {
                        if (kw.IndexOf(str) > -1)
                        {
                            kw = kw.Replace(str, str[1..]);
                            break;
                        }
                    }
                }
                #endregion

                #region 取代指定文字 && 特殊地名/簡稱處理/特殊區域型地標

                #region 同音不同字路名
                if (kw.IndexOf("木柵路") == -1 && kw.IndexOf("文山區") == -1 && kw.IndexOf("木柵") > -1 && kw.IndexOf("捷運") == -1)
                {
                    kw = kw.Replace("木柵", "文山區");
                }
                if (kw.IndexOf("木柵路") == -1 && kw.IndexOf("文山區") > -1 && kw.IndexOf("木柵") > -1 && kw.IndexOf("捷運") == -1)
                {
                    kw = kw.Replace("木柵", "");
                }
                if (kw.IndexOf("台北市") == -1 && kw.IndexOf("新北市") == -1 && kw.IndexOf("竹北市") == -1 && kw.IndexOf("北市") > -1 && kw.IndexOf("區") > -1)
                {
                    kw = kw.Replace("北市", "");
                }
                if (kw.IndexOf("大直街") == -1 && kw.IndexOf("大直") > -1 && kw.IndexOf("捷運") == -1)
                {
                    kw = kw.Replace("大直", "");
                }
                if (kw.IndexOf("中路里中路") == -1 && kw.IndexOf("中路里") > -1)
                {
                    kw = kw.Replace("中路里", "中路里中路");
                }
                if (kw.IndexOf("桃園區") == -1 && kw.IndexOf("桃園市") == -1 && kw.IndexOf("桃園") > -1 && kw.IndexOf("桃園街") == -1 && kw.IndexOf("桃園醫院") == -1 && kw.IndexOf("校區") == -1 && kw.IndexOf("監理站") == -1 && kw.IndexOf("院區") == -1)
                {
                    kw = kw.Replace("桃園", "桃園市");
                }
                if (kw.IndexOf("新竹") > -1 && kw.IndexOf("民石") > -1 && kw.IndexOf("桃山村") == -1)
                {
                    if (kw.IndexOf("桃山") > -1) { kw = kw.Replace("桃山", ""); }
                    kw = kw.Replace("民石", "桃山村民石");
                }
                if (kw.IndexOf("高雄路竹") > -1 && kw.IndexOf("高雄路竹區") == -1)
                {
                    kw = kw.Replace("高雄路竹", "高雄市路竹區");
                }
                if (checkNum > -1 && kw.IndexOf("竹北") > -1 && kw.IndexOf("新竹") == -1 && kw.IndexOf("竹北市") == -1 && kw.IndexOf("竹北街") == -1 && kw.IndexOf("竹北路") == -1 && kw.IndexOf("竹北巷") == -1 && kw.IndexOf("竹北二街") == -1)
                {
                    kw = kw.Replace("竹北", "新竹縣竹北市");
                }
                if (kw.IndexOf("竹北市竹北市") > -1) { kw = kw.Replace("竹北市竹北市", "竹北市"); }
                if (kw.IndexOf("梅區楊梅區") > -1) { kw = kw.Replace("梅區楊梅區", "楊梅區"); }
                if (kw.IndexOf("竹北市") > -1 && kw.IndexOf("新竹縣") == -1) { kw = kw.Replace("竹北市", "新竹縣竹北市"); }
                if (checkNum > -1 && kw.IndexOf("竹東") > -1 && kw.IndexOf("新竹") == -1 && kw.IndexOf("台南") == -1 && kw.IndexOf("竹東路") == -1 && kw.IndexOf("竹東巷") == -1)
                {
                    kw = kw.Replace("竹東", "新竹縣竹東鎮");
                }
                if (kw.IndexOf("萬榮萬榮") > -1 && kw.IndexOf("萬榮街") == -1)
                {
                    if (kw.IndexOf("花蓮萬榮") > -1)
                    {
                        kw = kw.Replace("花蓮萬榮", "萬榮");
                    }
                    if (kw.IndexOf("萬榮鄉") > -1)
                    {
                        kw = kw.Replace("萬榮萬榮", "萬榮");
                    }
                    else
                    {
                        kw = kw.Replace("萬榮萬榮", "萬榮鄉萬榮");
                    }
                }
                if (kw.IndexOf("阿里山阿里山") > -1)
                {
                    if (kw.IndexOf("阿里山鄉") > -1)
                    {
                        kw = kw.Replace("阿里山阿里山", "阿里山");
                    }
                    else
                    {
                        kw = kw.Replace("阿里山阿里山", "阿里山鄉阿里山");
                    }
                }
                if (kw.IndexOf("大埔美園區") > -1 && kw.IndexOf("大林鎮") == -1)
                {
                    if (kw.IndexOf("大林") > -1)
                    {
                        kw = kw.Replace("大林", "大林鎮");
                    }
                    else
                    {
                        kw = kw.Replace("大埔美園區", "大林鎮大埔美園區");
                    }
                }
                if (kw.IndexOf("中壢") > -1 && kw.IndexOf("民南路") > -1 && kw.IndexOf("榮民南路") == -1)
                {
                    kw = kw.Replace("民南路", "榮民南路");
                }
                if (kw.IndexOf("捷運") > -1 && kw.IndexOf("亞東") > -1 && kw.IndexOf("亞東醫院") == -1)
                {
                    kw = kw.Replace("亞東", "亞東醫院");
                }
                if ((kw.IndexOf("新北市") > -1 || kw.IndexOf("三峽") > -1) && kw.IndexOf("弘道路") > -1)
                {
                    kw = kw.Replace("弘道路", "弘道");
                }
                if ((kw.IndexOf("屏東") > -1 || kw.IndexOf("萬丹") > -1) && kw.IndexOf("大龍") > -1)
                {
                    kw = kw.Replace("大龍", "大隆");
                }
                if ((kw.IndexOf("台南") > -1 || kw.IndexOf("左鎮") > -1) && kw.IndexOf("岡林裡") > -1)
                {
                    kw = kw.Replace("岡林裡", "岡林里");
                }
                if ((kw.IndexOf("台南") > -1 || kw.IndexOf("善化") > -1) && kw.IndexOf("牛莊") > -1)
                {
                    kw = kw.Replace("牛莊", "牛庄");
                }
                if ((kw.IndexOf("台中") > -1 || kw.IndexOf("北屯") > -1) && kw.IndexOf("後莊") > -1 && kw.IndexOf("後莊路") == -1)
                {
                    kw = kw.Replace("後莊", "后庄");
                }
                if ((kw.IndexOf("新北") > -1 || kw.IndexOf("板橋") > -1) && kw.IndexOf("和宜") > -1)
                {
                    kw = kw.Replace("和宜", "合宜");
                }
                if (kw.IndexOf("苗栗") > -1 && kw.IndexOf("南莊") > -1)
                {
                    kw = kw.Replace("南莊", "南庄");
                }
                if (kw.IndexOf("中和") > -1 && kw.IndexOf("華興街") > -1)
                {
                    kw = kw.Replace("華興街", "華新街");
                }
                if (kw.IndexOf("迴龍") > -1 && kw.IndexOf("桃園") == -1 && kw.IndexOf("龜山") == -1 && kw.IndexOf("捷運") == -1)
                {
                    kw = kw.Replace("迴龍", "桃園市龜山區");
                }
                if (kw.IndexOf("湯城") > -1 && kw.IndexOf("桃園") == -1 && kw.IndexOf("嘉義") == -1 && kw.IndexOf("路") == -1 && kw.IndexOf("段") == -1)
                {
                    kw = kw.Replace("湯城", "重新路五段609巷");
                }
                if (kw.IndexOf("東海十街") > -1 && kw.IndexOf("花蓮縣") == -1 && kw.IndexOf("吉安鄉") == -1)
                {
                    kw = kw.Replace("東海十街", "花蓮縣吉安鄉東海十街");
                }
                if (kw.IndexOf("新北市") > -1 && kw.IndexOf("忠誠街") > -1)
                {
                    kw = kw.Replace("忠誠街", "中誠街");
                }
                if (kw.IndexOf("蘆洲") > -1 && kw.IndexOf("明義街") > -1)
                {
                    kw = kw.Replace("明義街", "民義街");
                }
                if (kw.IndexOf("中和") > -1 && kw.IndexOf("民義街") > -1)
                {
                    kw = kw.Replace("民義街", "明義街");
                }
                if (kw.IndexOf("區運路") > -1)
                {
                    if (kw.IndexOf("新北") > -1 && kw.IndexOf("新北市") == -1)
                    {
                        kw = kw.Replace("新北", "新北市");
                    }
                    if (kw.IndexOf("板橋區運路") > -1)
                    {
                        kw = kw.Replace("板橋區運路", "板橋區區運路");
                    }
                }
                if (kw.IndexOf("區東路") > -1)
                {
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("高雄市") == -1)
                    {
                        kw = kw.Replace("高雄", "高雄市");
                    }
                    if (kw.IndexOf("楠梓區東路") > -1)
                    {
                        kw = kw.Replace("楠梓區東路", "楠梓區區東路");
                    }
                }
                if (kw.IndexOf("湯泉二期") > -1 && kw.IndexOf("溪園路") == -1)
                {
                    kw = kw.Replace("湯泉二期", "溪園路");
                }
                if (kw.IndexOf("湯泉美地") > -1 && kw.IndexOf("西園路") == -1)
                {
                    kw = kw.Replace("西園路", "溪園路");
                }
                if (kw.IndexOf("研究路") > -1 && (kw.IndexOf("北市") > -1 || kw.IndexOf("台北") > -1 || kw.IndexOf("南港") > -1))
                {
                    kw = kw.Replace("研究路", "研究院路");
                }
                if (kw.IndexOf("正興路") > -1 && (kw.IndexOf("桃市") > -1 || kw.IndexOf("桃園") > -1 || kw.IndexOf("龜山") > -1))
                {
                    kw = kw.Replace("正興路", "振興路");
                }
                if (kw.IndexOf("凱旋四路") > -1 && kw.IndexOf("振興路") > -1)
                {
                    kw = kw.Replace("振興路", "鎮興路");
                }
                if (kw.IndexOf("平鎮") > -1 && kw.IndexOf("日新街") > -1)
                {
                    kw = kw.Replace("日新街", "日星街");
                }
                if (kw.IndexOf("楊梅") > -1 && kw.IndexOf("日星街") > -1)
                {
                    kw = kw.Replace("日星街", "日新街");
                }
                if ((kw.IndexOf("新竹") > -1 || kw.IndexOf("竹北") > -1) && kw.IndexOf("文心路") > -1)
                {
                    kw = kw.Replace("文心路", "文興路");
                }
                if (kw.IndexOf("龜山") > -1 && kw.IndexOf("文心路") > -1)
                {
                    kw = kw.Replace("文心路", "文興路");
                }
                if (kw.IndexOf("桃園區") > -1 && kw.IndexOf("建興街") > -1)
                {
                    kw = kw.Replace("建興街", "建新街");
                }
                if (kw.IndexOf("八德區") > -1 && kw.IndexOf("建新街") > -1)
                {
                    kw = kw.Replace("建新街", "建興街");
                }
                if (kw.IndexOf("聖保祿") > -1 && kw.IndexOf("建興街") > -1)
                {
                    kw = kw.Replace("建興街", "建新街");
                }
                if (kw.IndexOf("建興路") > -1 && kw.IndexOf("內埔") > -1)
                {
                    kw = kw.Replace("建興路", "建新路");
                }
                if (kw.IndexOf("建興路") > -1 && kw.IndexOf("新埤") > -1)
                {
                    kw = kw.Replace("建興路", "建新路");
                }
                if (kw.IndexOf("建興路") > -1 && (kw.IndexOf("新竹市") > -1 || kw.IndexOf("東區") > -1))
                {
                    kw = kw.Replace("建興路", "建新路");
                }
                if ((kw.IndexOf("台南") > -1 || kw.IndexOf("永康") > -1) && kw.IndexOf("北新路") > -1)
                {
                    kw = kw.Replace("北新路", "北興路");
                }
                if ((kw.IndexOf("桃園") > -1 || kw.IndexOf("八德") > -1) && kw.IndexOf("新豐路") > -1)
                {
                    kw = kw.Replace("新豐路", "興豐路");
                }
                if ((kw.IndexOf("台中") > -1 || kw.IndexOf("大里") > -1) && kw.IndexOf("東龍路") > -1)
                {
                    kw = kw.Replace("東龍路", "東榮路");
                }
                if (kw.IndexOf("台北") > -1 && kw.IndexOf("士林") > -1 && kw.IndexOf("士林區") == -1)
                {
                    kw = kw.Replace("士林", "士林區");
                }
                if (kw.IndexOf("楊梅") > -1 && kw.IndexOf("福林路") > -1)
                {
                    kw = kw.Replace("福林路", "福羚路");
                }
                if (kw.IndexOf("北投") > -1 && kw.IndexOf("北投區") == -1 && kw.IndexOf("北投街") == -1 && kw.IndexOf("北投路") == -1)
                {
                    kw = kw.Replace("北投", "北投區");
                }
                if (kw.IndexOf("新莊街") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("龍井") > -1))
                {
                    kw = kw.Replace("新莊街", "新庄街");
                }
                if (kw.IndexOf("東興路") > -1 && kw.IndexOf("新竹") > -1 && kw.IndexOf("竹北") == -1)
                {
                    kw = kw.Replace("東興路", "東新路");
                }
                if (kw.IndexOf("新中街") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("光復路") > -1))
                {
                    kw = kw.Replace("新中街", "興中街");
                }
                if (kw.IndexOf("新中街") > -1 && kw.IndexOf("嘉義市") > -1)
                {
                    kw = kw.Replace("新中街", "興中街");
                }
                if (kw.IndexOf("明德路") > -1 && kw.IndexOf("中和") > -1)
                {
                    kw = kw.Replace("明德路", "民德路");
                }
                if (kw.IndexOf("文山一街") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("龜山") > -1))
                {
                    kw = kw.Replace("文山一街", "文三一街");
                }
                if (kw.IndexOf("文三一街") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("新埔") > -1))
                {
                    kw = kw.Replace("文三一街", "文山一街");
                }
                if (kw.IndexOf("南雅街") > -1 && kw.IndexOf("新竹") > -1) { kw = kw.Replace("南雅街", "湳雅街"); }
                if (kw.IndexOf("龍興街") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("楠梓") > -1)) { kw = kw.Replace("龍興街", "榮新街"); }
                if (kw.IndexOf("大莊路") > -1 && kw.IndexOf("南投") > -1) { kw = kw.Replace("大莊路", "大庄路"); }
                if (kw.IndexOf("大莊路") > -1 && (kw.IndexOf("新竹") > -1 || kw.IndexOf("香山") > -1)) { kw = kw.Replace("大莊路", "大庄路"); }
                if (kw.IndexOf("屏東市") > -1 && kw.IndexOf("屏東縣") == -1) { kw = kw.Replace("屏東市", "屏東縣屏東市"); }
                if (checkNum > -1 && kw.IndexOf("新北產業園區") > -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("機場") == -1) { kw = kw.Replace("新北產業園區", ""); }
                if (checkNum > -1 && kw.IndexOf("協和里") > -1) { kw = kw.Replace("協和里", ""); }
                if (kw.IndexOf("興隆路") > -1 && kw.IndexOf("桃園") > -1 && (kw.IndexOf("新屋") > -1 || kw.IndexOf("楊梅") > -1)) { kw = kw.Replace("興隆路", "新榮路"); }
                if (kw.IndexOf("新榮路") > -1 && kw.IndexOf("桃園") > -1 && (kw.IndexOf("桃園區") > -1 || kw.IndexOf("龍潭") > -1)) { kw = kw.Replace("新榮路", "興隆路"); }
                if (kw.IndexOf("板橋市") > -1 && kw.IndexOf("板橋市民") == -1) { kw = kw.Replace("板橋市", "板橋區"); }
                if (checkNum > -1 && kw.IndexOf("中壢") > -1 && kw.IndexOf("中壢區") == -1) { kw = kw.Replace("中壢", "中壢區"); }
                if (kw.IndexOf("桃園區") > -1 && kw.IndexOf("桃園市") == -1) { kw = kw.Replace("桃園區", "桃園市桃園區"); }
                if (kw.IndexOf("新竹市") > -1 && kw.IndexOf("科環路") > -1) { kw = kw.Replace("新竹市", "新竹縣"); }
                if (checkNum > -1 && kw.IndexOf("新竹工業區") > -1) { kw = kw.Replace("新竹工業區", ""); }
                if (checkNum > -1 && kw.IndexOf("南崁") > -1 && kw.IndexOf("南崁路") == -1 && kw.IndexOf("南崁後街") == -1 && kw.IndexOf("逗號") == -1) { kw = kw.Replace("南崁", "蘆竹區"); }
                if (kw.IndexOf("鄭州路") > -1 && kw.IndexOf("高雄") > -1) { kw = kw.Replace("鄭州路", "鎮州路"); }
                if (kw.IndexOf("鎮州路") > -1 && kw.IndexOf("台北") > -1) { kw = kw.Replace("鎮州路", "鄭州路"); }
                if (kw.IndexOf("福興路") > -1 && kw.IndexOf("新和街") > -1 && kw.IndexOf("路口") > -1)
                {
                    kw = kw.Replace("福興路", "福星路");
                    kw = kw.Replace("新和街", "慶和街");
                }
                if (kw.IndexOf("勇全路") > -1 && kw.IndexOf("屏東") > -1) { kw = kw.Replace("勇全路", "永全路"); }
                if (kw.IndexOf("永全路") > -1 && kw.IndexOf("高雄") > -1) { kw = kw.Replace("永全路", "勇全路"); }
                if (kw.IndexOf("南平路") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("內門") > -1 || kw.IndexOf("左營") > -1 || kw.IndexOf("鼓山") > -1)) { kw = kw.Replace("南平路", "南屏路"); }
                if (kw.IndexOf("圓山街") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("大園") > -1)) { kw = kw.Replace("圓山街", "園三街"); }
                if (kw.IndexOf("寶昌") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("觀音") > -1)) { kw = kw.Replace("寶昌", "寶倉"); }
                if (kw.IndexOf("隆林路") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("中壢") > -1)) { kw = kw.Replace("隆林路", "龍陵路"); }
                if (kw.IndexOf("大興街") > -1 && kw.IndexOf("岡山") > -1) { kw = kw.Replace("大興街", "大新街"); }
                if (kw.IndexOf("大新街") > -1 && (kw.IndexOf("三民") > -1 || kw.IndexOf("大社") > -1)) { kw = kw.Replace("大新街", "大興街"); }
                if (kw.IndexOf("檳榔村") > -1 && (kw.IndexOf("台東") > -1 || kw.IndexOf("卑南") > -1)) { kw = kw.Replace("檳榔村", "賓朗村"); }
                if (kw.IndexOf("下檳榔") > -1 && (kw.IndexOf("台東") > -1 || kw.IndexOf("卑南") > -1)) { kw = kw.Replace("下檳榔", "下賓朗"); }
                if (kw.IndexOf("鼎新路") > -1 && (kw.IndexOf("高雄") == -1 || kw.IndexOf("三民") == -1)) { kw = kw.Replace("鼎新路", "頂興路"); }
                if (kw.IndexOf("頂興路") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("三民") > -1)) { kw = kw.Replace("頂興路", "鼎新路"); }
                if ((kw.IndexOf("溪州路") > -1 || kw.IndexOf("溪洲路") > -1) && kw.IndexOf("大肚") == -1)
                {
                    var isChange = false;
                    if (kw.IndexOf("桃園") > -1 || kw.IndexOf("大園") > -1) { kw = kw.Replace("溪洲路", "溪州路"); isChange = true; }
                    if (kw.IndexOf("新竹") > -1 || kw.IndexOf("東區") > -1 || kw.IndexOf("竹北") > -1) { kw = kw.Replace("溪洲路", "溪州路"); isChange = true; }
                    if (!isChange) { kw = kw.Replace("溪州路", "溪洲路"); }
                }
                if (kw.IndexOf("新民路") > -1 || kw.IndexOf("新明路") > -1 || kw.IndexOf("興民路") > -1)
                {
                    var isChange = false;
                    if (kw.IndexOf("台東") > -1 || kw.IndexOf("鹿野") > -1) { kw = kw.Replace("新民路", "興民路").Replace("新明路", "興民路"); isChange = true; }
                    if (kw.IndexOf("大里") > -1) { kw = kw.Replace("新民路", "新明路"); isChange = true; }
                    if (kw.IndexOf("內湖") > -1) { kw = kw.Replace("新民路", "新明路"); isChange = true; }
                    if (kw.IndexOf("桃園") > -1 || kw.IndexOf("中壢") > -1) { kw = kw.Replace("新民路", "新明路"); isChange = true; }
                    if (kw.IndexOf("澎湖") > -1 || kw.IndexOf("馬公") > -1) { kw = kw.Replace("新民路", "新明路"); isChange = true; }
                    if (!isChange) { kw = kw.Replace("新明路", "新民路"); }
                }
                if (kw.IndexOf("福新街") > -1 || kw.IndexOf("福興街") > -1)
                {
                    if (kw.IndexOf("台中") > -1 && kw.IndexOf("南區") > -1) { kw = kw.Replace("福興街", "福新街"); }
                    else { kw = kw.Replace("福新街", "福興街"); }
                }
                if (kw.IndexOf("凹子底") > -1 && kw.IndexOf("郵局") > -1) { kw = kw.Replace("凹子底", "凹仔底"); }
                if (kw.IndexOf("凹仔底") > -1 && kw.IndexOf("郵局") == -1) { kw = kw.Replace("凹仔底", "凹子底"); }
                if (kw.IndexOf("忠明街") > -1 && kw.IndexOf("彰化") > -1) { kw = kw.Replace("忠明街", "中民街"); }
                if (kw.IndexOf("南興街") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("基隆") > -1 || kw.IndexOf("旗山") > -1 || kw.IndexOf("仁愛") > -1 || kw.IndexOf("新營") > -1))
                {
                    kw = kw.Replace("南興街", "南新街");
                }
                if (kw.IndexOf("建功路") > -1 || kw.IndexOf("建工路") > -1)
                {
                    if (kw.IndexOf("內埔") > -1 || kw.IndexOf("高雄") > -1 || kw.IndexOf("三民") > -1 || kw.IndexOf("嘉義") > -1 || kw.IndexOf("太保") > -1)
                    {
                        kw = kw.Replace("建功路", "建工路");
                    }
                    else
                    {
                        kw = kw.Replace("建工路", "建功路");
                    }
                }
                if (kw.IndexOf("福興路") > -1 || kw.IndexOf("福星路") > -1)
                {
                    if (kw.IndexOf("沙鹿") > -1 || kw.IndexOf("潮州") > -1 || kw.IndexOf("西屯") > -1)
                    {
                        kw = kw.Replace("福興路", "福星路");
                    }
                    else
                    {
                        kw = kw.Replace("福星路", "福興路");
                    }
                }
                if (kw.IndexOf("新昌街") > -1 || kw.IndexOf("興昌街") > -1)
                {
                    if (kw.IndexOf("三民") > -1 || kw.IndexOf("員林") > -1 || kw.IndexOf("彰化") > -1)
                    {
                        kw = kw.Replace("新昌街", "興昌街");
                    }
                    else
                    {
                        kw = kw.Replace("興昌街", "新昌街");
                    }
                }
                if (kw.IndexOf("建民路") > -1 || kw.IndexOf("建明路") > -1)
                {
                    if ((kw.IndexOf("內埔") > -1 && kw.IndexOf("路1號") == -1 && kw.IndexOf("路2號") == -1 && kw.IndexOf("路3號") == -1)
                        || kw.IndexOf("平鎮") > -1 || kw.IndexOf("桃園") > -1)
                    {
                        kw = kw.Replace("建民路", "建明路");
                    }
                    else
                    {
                        kw = kw.Replace("建明路", "建民路");
                    }
                }
                if (kw.IndexOf("義興街") > -1 || kw.IndexOf("億興街") > -1)
                {
                    if (kw.IndexOf("苗栗") > -1 || kw.IndexOf("頭屋") > -1)
                    {
                        kw = kw.Replace("義興街", "億興街");
                    }
                    else
                    {
                        kw = kw.Replace("億興街", "義興街");
                    }
                }
                if (kw.IndexOf("明德一街") > -1 || kw.IndexOf("民德一街") > -1 || kw.IndexOf("明德二街") > -1 || kw.IndexOf("民德二街") > -1)
                {
                    if (kw.IndexOf("屏東") > -1 || kw.IndexOf("東港") > -1)
                    {
                        kw = kw.Replace("民德一街", "明德一街");
                        kw = kw.Replace("民德二街", "明德二街");
                    }
                    else
                    {
                        kw = kw.Replace("明德一街", "民德一街");
                        kw = kw.Replace("明德二街", "民德二街");
                    }
                }
                if (kw.IndexOf("庄內") > -1 && (kw.IndexOf("外埔") > -1 || kw.IndexOf("彰化") > -1 || kw.IndexOf("溪州") > -1)) { kw = kw.Replace("庄內", "莊內"); }
                if (kw.IndexOf("新莊一路") > -1 || kw.IndexOf("新莊二路") > -1 || kw.IndexOf("新莊三路") > -1) { kw = kw.Replace("新莊", "新庄"); }
                if (kw.IndexOf("頂莊") > -1 && kw.IndexOf("雲頂莊") == -1) { kw = kw.Replace("頂莊", "頂庄"); }
                if (checkNum > -1 && kw.IndexOf("興南路") > -1 && kw.IndexOf("復興南路") == -1 && kw.IndexOf("新興南路") == -1 && kw.IndexOf("西興南路") == -1 && kw.IndexOf("蘆興南路") == -1 && kw.IndexOf("台興南路") == -1 && kw.IndexOf("虎興南路") == -1 && kw.IndexOf("福興南路") == -1 && kw.IndexOf("中興南路") == -1)
                {
                    if (kw.IndexOf("台中市") > -1 || kw.IndexOf("大里") > -1 || kw.IndexOf("新營") > -1 || kw.IndexOf("萬丹") > -1 || kw.IndexOf("蘆竹") > -1 || kw.IndexOf("雲林") > -1 || kw.IndexOf("北港") > -1 || kw.IndexOf("宜蘭") > -1 ||
                         kw.IndexOf("壯圍") > -1 || kw.IndexOf("南投") > -1 || kw.IndexOf("水里") > -1 || kw.IndexOf("桃園區") > -1 || kw.IndexOf("龜山") > -1 || kw.IndexOf("嘉義") > -1 || kw.IndexOf("布袋") > -1 || kw.IndexOf("芳苑") > -1)
                    {
                        kw = kw.Replace("興南路", "新南路");
                    }
                }
                if (kw.IndexOf("崇義街") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("岡山") > -1)) { kw = kw.Replace("崇義街", "重義街"); }
                if (kw.IndexOf("新莊路") > -1 || kw.IndexOf("新庄路") > -1)
                {
                    if (kw.IndexOf("橋頭") > -1 || kw.IndexOf("新北") > -1 || kw.IndexOf("新莊區") > -1)
                    {
                        kw = kw.Replace("新庄路", "新莊路");
                    }
                    else
                    {
                        kw = kw.Replace("新莊路", "新庄路");
                    }
                }
                if (kw.IndexOf("新莊街") > -1 || kw.IndexOf("新庄街") > -1)
                {
                    if (kw.IndexOf("新竹市") > -1 || kw.IndexOf("東區") > -1)
                    {
                        kw = kw.Replace("新庄街", "新莊街");
                    }
                    else
                    {
                        kw = kw.Replace("新莊街", "新庄街");
                    }
                }
                if (kw.IndexOf("新民街") > -1 || kw.IndexOf("興民街") > -1)
                {
                    if (kw.IndexOf("台中") > -1 && kw.IndexOf("中區") > -1)
                    {
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getNewAddr.Num) && getNewAddr.Num.IndexOf("之") == -1)
                        {
                            var ss = int.Parse(getNewAddr.Num.Replace("號", ""));
                            if (ss >= 142 && ss <= 160 && ss % 2 == 0)
                            {
                                kw = kw.Replace("興民街", "新民街");
                            }
                            else
                            {
                                kw = kw.Replace("新民街", "興民街");
                            }
                        }
                        else
                        {
                            kw = kw.Replace("新民街", "興民街");
                        }
                    }
                    else
                    {
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("麻豆") > -1 || kw.IndexOf("新莊區") > -1)
                        {
                            kw = kw.Replace("新民街", "興民街");
                        }
                        else
                        {
                            kw = kw.Replace("興民街", "新民街");
                        }
                    }
                }
                if (kw.IndexOf("文三三街") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("南屯") > -1)) { kw = kw.Replace("文三三街", "文山三街"); }
                if (kw.IndexOf("文山三街") > -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("龜山") > -1)) { kw = kw.Replace("文山三街", "文三三街"); }
                if (kw.IndexOf("重新路") > -1 && (kw.IndexOf("台中") > -1 || kw.IndexOf("北屯") > -1 || kw.IndexOf("北區") > -1)) { kw = kw.Replace("重新路", "崇興路"); }
                #endregion

                #region 補單位
                if (checkNum > -1)
                {
                    if (kw.IndexOf("大雅區") == -1 && kw.IndexOf("大雅") > -1 && kw.IndexOf("大雅街") == -1 && kw.IndexOf("大雅路") == -1)
                    {
                        var pattern = @"大雅.{1}街"; var regex = new Regex(pattern);
                        if (!regex.IsMatch(kw)) { kw = kw.Replace("大雅", "大雅區"); }
                    }
                    if (kw.IndexOf("萬華區") == -1 && kw.IndexOf("萬華") > -1 && kw.IndexOf("萬華街") == -1 && kw.IndexOf("萬華路") == -1) { kw = kw.Replace("萬華", "萬華區"); }
                    if (kw.IndexOf("彰化") > -1 && kw.IndexOf("彰化市") == -1 && kw.IndexOf("彰化縣") == -1) { kw = kw.Replace("彰化", "彰化縣"); }
                    if (kw.IndexOf("市府路") > -1 && (kw.IndexOf("中區") > -1 || kw.IndexOf("西區") > -1) && kw.IndexOf("台中市") == -1)
                    {
                        if (kw.IndexOf("台中") > -1)
                        {
                            kw = kw.Replace("台中", "台中市");
                        }
                        else
                        {
                            if (kw.IndexOf("中區") > -1) { kw = kw.Replace("中區", "台中市中區"); }
                            if (kw.IndexOf("西區") > -1) { kw = kw.Replace("西區", "台中市西區"); }
                        }
                    }
                    if (kw.IndexOf("市府路") > -1 && (kw.IndexOf("信義") > -1 || kw.IndexOf("台北") > -1) && kw.IndexOf("台北市") == -1)
                    {
                        if (kw.IndexOf("台北") > -1)
                        {
                            kw = kw.Replace("台北", "台北市");
                        }
                        else
                        {
                            kw = kw.Replace("信義", "台北市信義");
                        }
                    }
                    if (kw.IndexOf("汐止區") == -1 && kw.IndexOf("汐止") > -1) { kw = kw.Replace("汐止", "汐止區"); }
                    if (kw.IndexOf("八里區") == -1 && kw.IndexOf("八里") > -1 && kw.IndexOf("八里大道") == -1 && kw.IndexOf("八里堆") == -1 && kw.IndexOf("南八里") == -1) { kw = kw.Replace("八里", "八里區"); }
                    if (kw.IndexOf("台南仁德區") > -1) { kw = kw.Replace("台南仁德區", "台南市仁德區"); }
                }
                #endregion

                #region 防止臭奶呆
                if (kw.IndexOf("花林") > -1)
                {
                    kw = kw.Replace("花林", "花蓮");
                }
                if (kw.IndexOf("基隆") > -1 && kw.IndexOf("和宜路") > -1)
                {
                    kw = kw.Replace("和宜路", "和一路");
                }
                if (kw.IndexOf("基隆") > -1 && kw.IndexOf("信義路") > -1)
                {
                    kw = kw.Replace("信義路", "信一路");
                }
                if (kw.IndexOf("桃園") > -1 && kw.IndexOf("文華路") > -1)
                {
                    kw = kw.Replace("文華路", "文化路");
                }
                if (kw.IndexOf("台北") > -1 && kw.IndexOf("新義路") > -1)
                {
                    kw = kw.Replace("新義路", "信義路");
                }
                if ((kw.IndexOf("台北") > -1 || kw.IndexOf("松山") > -1) && kw.IndexOf("民安街") > -1)
                {
                    kw = kw.Replace("民安街", "寧安街");
                }
                if ((kw.IndexOf("桃園") > -1 || kw.IndexOf("平鎮") > -1) && kw.IndexOf("高松路") > -1)
                {
                    kw = kw.Replace("高松路", "高雙路");
                }
                if (kw.IndexOf("商號") > -1)
                {
                    // 如果非特殊地標就轉換
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    if ((!string.IsNullOrEmpty(getNewAddr.City) || !string.IsNullOrEmpty(getNewAddr.Dist)) && !string.IsNullOrEmpty(getNewAddr.Road))
                    {
                        kw = kw.Replace("商號", "3號");
                    }
                }
                if ((kw.IndexOf("高雄") > -1 || kw.IndexOf("苓雅") > -1) && kw.IndexOf("臨安路") > -1)
                {
                    kw = kw.Replace("臨安路", "苓安路");
                }
                if ((kw.IndexOf("台中") > -1 || kw.IndexOf("北區") > -1) && kw.IndexOf("義德路") > -1)
                {
                    kw = kw.Replace("義德路", "育德路");
                }
                #endregion

                #region 某些特殊地標在資料庫有特殊符號需額外處理

                #region 清除重複字詞
                var (rw, rc) = FindRepeatingWords(kw);
                if (!string.IsNullOrEmpty(rw) && (rw == "暖" && (kw.IndexOf("暖暖區") == -1 || kw.IndexOf("暖暖街") == -1)))
                {
                    kw = kw.Replace(string.Join("", Enumerable.Repeat(rw, rc)), rw);
                }
                #endregion
                // 需先排除交叉路口
                var isCrossRoadKW = false;
                var mergedCrossRoadKeyWord = noNumKeyWord.Concat(delCrossRoadKeyWord).Concat(crossRoadKeyWord).Distinct();
                foreach (var str in mergedCrossRoadKeyWord)
                {
                    if (kw.IndexOf(str) > -1)
                    {
                        isCrossRoadKW = true;
                        break;
                    }
                }
                var isReplaceMarkName = false;
                if (checkMarkName && checkNum == -1 && !isCrossRoadKW)
                {
                    isReplaceMarkName = true;
                    if (kw == "101") { kw = "台北101"; }
                    if (kw.IndexOf("銀行") > -1 && kw.IndexOf("分行") > -1) { kw = kw.Replace("銀行", "銀行-"); }
                    if (kw.IndexOf("有巢氏房屋") > -1) { kw = kw.Replace("有巢氏房屋", "有巢氏房屋-"); }
                    if (kw.IndexOf("中國信託") > -1 && kw.IndexOf("分行") > -1) { kw = kw.Replace("銀行-", "").Replace("銀行", "").Replace("中國信託", "中國信託-"); }
                    if (kw.IndexOf("捷運站口") > -1) { kw = kw.Replace("捷運站口", "捷運站"); }
                    if (kw.IndexOf("巷子口") > -1 && (kw.IndexOf("道") > -1 || kw.IndexOf("路") > -1 || kw.IndexOf("街") > -1)) { kw = kw.Replace("巷子口", "巷口"); }
                    if (kw.IndexOf("COSTCO") > -1) { kw = kw.Replace("COSTCO", "好市多"); }
                    if (kw.IndexOf("好市多") > -1 && kw.IndexOf("店") == -1)
                    {
                        var arr = kw.Split("好市多");
                        if (string.IsNullOrEmpty(arr[1]))
                        {
                            kw = "好市多" + arr[0] + "店";
                        }
                        else
                        {
                            kw += "店";
                        }
                    }
                    if (kw.IndexOf("頂好") > -1 && kw.IndexOf("店") > -1 && kw.IndexOf("WELLCOME") == -1)
                    {
                        kw = kw.Replace("頂好", "頂好Wellcome");
                    }
                    if (kw.IndexOf("汽車旅館") > -1)
                    {
                        var arr = kw.Split("汽車旅館");
                        if (!string.IsNullOrEmpty(arr[1]) && arr[1].IndexOf("館") > -1)
                        {
                            kw = kw.Replace("汽車旅館", "汽車旅館-");
                        }
                    }
                    if (kw.IndexOf("西門錢櫃") > -1 || kw.IndexOf("錢櫃西門店") > -1 || kw.IndexOf("中華錢櫃") > -1)
                    {
                        kw = "錢櫃-台北中華新館";
                    }
                    if (kw.IndexOf("新竹湖口火車站") > -1 && kw.IndexOf("新竹縣") == -1)
                    {
                        kw = "湖口火車站";
                    }
                    if (kw.IndexOf("OK便利商店") > -1)
                    {
                        kw = kw.Replace("OK便利商店", "OK");
                    }
                    if (kw.IndexOf("市火車站") >= 2)
                    {
                        var _city = kw.Substring(kw.IndexOf("市火車站") - 2, 2);
                        kw = _city + kw[(kw.IndexOf("市火車站") + 1)..];
                    }
                    if (kw.IndexOf("SEVEN") > -1 || kw.IndexOf("7-ELEVEN") > -1)
                    {
                        kw = kw.Replace("SEVEN", "7－ELEVEN-");
                        kw = kw.Replace("7-ELEVEN", "7－ELEVEN-");
                    }
                    if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("北回歸線") > -1)
                    {
                        kw = "北回歸線太陽館";
                    }

                    if (kw.IndexOf("麥當勞") > -1)
                    {
                        if (kw.IndexOf("店") > -1 && kw.IndexOf("門市") == -1)
                        {
                            kw = kw.Replace("店", "門市");
                        }
                        foreach (var c in allCity.Concat(allCityCounty).Concat(allCityTwo))
                        {
                            if (kw.IndexOf(c) > -1)
                            {
                                var idx = kw.IndexOf("麥當勞");
                                if (kw.IndexOf(c) < idx && (kw.Length - idx - 3) > 0)
                                {
                                    kw = "麥當勞-" + c + kw.Substring(idx + 3, kw.Length - idx - 3);
                                }
                                else
                                {
                                    kw = kw.Replace("麥當勞", "麥當勞-");
                                }
                                break;
                            }
                        }
                    }
                    if (kw.IndexOf("中山女高") > -1 || (kw.IndexOf("中山女子") > -1 && kw.IndexOf("校友會") == -1)) { kw = "市立中山女中"; }
                    if (kw.IndexOf("建國高中") > -1 || kw.IndexOf("建國中學") > -1) { kw = "市立建國中學"; }
                    if (kw.IndexOf("北一女") > -1) { kw = "市立北一女中"; }
                    if (kw.IndexOf("莊敬中學") > -1) { kw = "新北市私立莊敬高級工業家事職業學校"; }
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("雄女高中") > -1) { kw = "高雄女子高級中學"; }
                    if (kw.IndexOf("高雄女子高級學校") > -1) { kw = "高雄女子高級中學"; }
                    if (kw.IndexOf("康橋") > -1 && kw.IndexOf("學校") > -1 && (kw.IndexOf("柴橋路") > -1 || kw.IndexOf("新竹") > -1)) { kw = "康橋國中"; }
                    if (kw.IndexOf("竹光國中") > -1) { kw = "竹光國中"; }
                    if (kw.IndexOf("西湖國中") > -1 && (kw.IndexOf("台北") > -1 || kw.IndexOf("內湖") > -1) && kw.IndexOf("實驗") == -1) { kw = "西湖實中"; }
                    if (kw.IndexOf("西湖") > -1 && kw.IndexOf("實驗") > -1 && (kw.IndexOf("國民中學") > -1 || kw.IndexOf("國中") > -1)) { kw = "西湖實中"; }
                    if (kw.IndexOf("大鵬國小") > -1 && (kw.IndexOf("新北") > -1 || kw.IndexOf("萬里") > -1 || kw.IndexOf("加投路") > -1)) { kw = "新北市萬里區大鵬國小"; }
                    if (kw.IndexOf("大鵬國小") > -1 && kw.IndexOf("新北") == -1 && kw.IndexOf("萬里") == -1) { kw = "台中市西屯區大鵬國小"; }
                    if (kw.IndexOf("清華大學") > -1)
                    {
                        var isChange = false;
                        if (kw.IndexOf("仁齋") > -1 || kw.IndexOf("實齋") > -1) { kw = "清華大學-仁齋實齋"; isChange = true; }
                        if (kw.IndexOf("明齋") > -1 || kw.IndexOf("平齋") > -1) { kw = "清華大學-明齋平齋"; isChange = true; }
                        if (kw.IndexOf("信齋") > -1) { kw = "清華大學-信齋"; isChange = true; }
                        if (kw.IndexOf("清齋") > -1) { kw = "清華大學-清齋"; isChange = true; }
                        if (kw.IndexOf("台達館") > -1) { kw = "清華大學-台達館"; isChange = true; }
                        if (kw.IndexOf("台積館") > -1) { kw = "清華大學-台積館"; isChange = true; }
                        if (kw.IndexOf("資電館") > -1) { kw = "清華大學-資電館"; isChange = true; }
                        if (kw.IndexOf("教育館") > -1) { kw = "清華大學-教育館"; isChange = true; }
                        if (kw.IndexOf("清華會館") > -1) { kw = "清華大學-清華會館"; isChange = true; }
                        if (kw.IndexOf("新體育館") > -1) { kw = "清華大學-新體育館"; isChange = true; }
                        if (kw.IndexOf("同位素館") > -1) { kw = "清華大學-同位素館"; isChange = true; }
                        if (kw.IndexOf("合金實驗館") > -1) { kw = "清華大學-合金實驗館"; isChange = true; }
                        if (kw.IndexOf("生物科技") > -1) { kw = "清華大學-生物科技館"; isChange = true; }
                        if (kw.IndexOf("生科一") > -1) { kw = "清華大學-生科一館"; isChange = true; }
                        if (kw.IndexOf("生科二") > -1) { kw = "清華大學-生科二館"; isChange = true; }
                        if (kw.IndexOf("材料實驗") > -1) { kw = "清華大學-材料實驗館"; isChange = true; }
                        if (kw.IndexOf("莊敬樓") > -1) { kw = "清華大學-莊敬樓"; isChange = true; }
                        if (kw.IndexOf("自強樓") > -1) { kw = "清華大學-自強樓"; isChange = true; }
                        if (kw.IndexOf("風雲樓") > -1) { kw = "清華大學-風雲樓"; isChange = true; }
                        if (kw.IndexOf("研發大樓") > -1) { kw = "清華大學-研發大樓"; isChange = true; }
                        if (kw.IndexOf("低碳綠能") > -1) { kw = "清華大學-低碳綠能大樓"; isChange = true; }
                        if (kw.IndexOf("第一綜合") > -1 && kw.IndexOf("側門") == -1) { kw = "清華大學-第一綜合行政大樓"; isChange = true; }
                        if (kw.IndexOf("第一綜合") > -1 && kw.IndexOf("側門") > -1) { kw = "清華大學-第一綜合行政大樓-側門"; isChange = true; }
                        if (kw.IndexOf("第二綜合") > -1) { kw = "清華大學-第二綜合大樓"; isChange = true; }
                        if (kw.IndexOf("第三綜合") > -1) { kw = "清華大學-第三綜合大樓"; isChange = true; }
                        if (kw.IndexOf("創新") > -1 || kw.IndexOf("育成") > -1) { kw = "清華大學-創新育成大樓"; isChange = true; }
                        if (kw.IndexOf("桌球") > -1) { kw = "清華大學-桌球館"; isChange = true; }
                        if (kw.IndexOf("棒球") > -1) { kw = "清華大學-棒球場"; isChange = true; }
                        if (kw.IndexOf("網球") > -1) { kw = "清華大學-網球場"; isChange = true; }
                        if (kw.IndexOf("籃球") > -1) { kw = "清華大學-籃球場"; isChange = true; }
                        if (kw.IndexOf("排球") > -1 || kw.IndexOf("田徑") > -1) { kw = "清華大學-田徑排球場"; isChange = true; }
                        if (kw.IndexOf("名人堂") > -1) { kw = "清華大學-名人堂"; isChange = true; }
                        if (kw.IndexOf("合勤演藝") > -1) { kw = "清華大學-合勤演藝廳"; isChange = true; }
                        if (kw.IndexOf("科儀中心") > -1) { kw = "清華大學-科儀中心"; isChange = true; }
                        if (kw.IndexOf("原科中心") > -1 || kw.IndexOf("反應器") > -1) { kw = "清華大學-原科中心反應器組"; isChange = true; }
                        if (kw.IndexOf("宿舍") > -1 && kw.IndexOf("西") > -1) { kw = "清華大學-西院宿舍"; isChange = true; }
                        if (kw.IndexOf("女宿") > -1 && kw.IndexOf("側門") == -1) { kw = "清華大學-清大女宿"; isChange = true; }
                        if (kw.IndexOf("女宿") > -1 && kw.IndexOf("側門") > -1) { kw = "清華大學-清大女宿側門"; isChange = true; }
                        if (kw.IndexOf("南大") > -1) { kw = "清華大學南大校區"; isChange = true; }
                        if (!isChange) { kw = "清華大學-校門口"; }
                    }
                    if (kw.IndexOf("台東大學") > -1)
                    {
                        var isChange = false;
                        if (kw.IndexOf("知本") > -1) { kw = "台東大學知本校區"; isChange = true; }
                        if (kw.IndexOf("特殊") > -1) { kw = "台東大學附特殊學校"; isChange = true; }
                        if (kw.IndexOf("體育") > -1 && kw.IndexOf("高中") > -1) { kw = "台東大學附體育高中"; isChange = true; }
                        if (kw.IndexOf("體育") > -1 && kw.IndexOf("國中") > -1) { kw = "台東大學附體育國中"; isChange = true; }
                        if (!isChange) { kw = "台東大學台東校區"; }
                    }
                    if (kw.IndexOf("中正大學") > -1 && kw.IndexOf("館") == -1 && kw.IndexOf("店") == -1 && kw.IndexOf("院") == -1) { kw = "中正大學-校門口"; }
                    if (kw.IndexOf("開南大學") > -1) { kw = "開南大學"; }
                    if (kw.IndexOf("台灣體育大學") > -1) { kw = "台灣體育運動大學台中校區"; }
                    if (kw.IndexOf("松山高級中學") > -1 || kw.IndexOf("松山高中") > -1) { kw = "松山高中"; }
                    if (kw.IndexOf("樹德科技大學") > -1 || kw.IndexOf("樹德科大") > -1) { kw = "樹德科技大學"; }
                    if (kw.IndexOf("長庚") > -1 && (kw.IndexOf("科大") > -1 || kw.IndexOf("大學") > -1) && kw.IndexOf("醫院") == -1)
                    {
                        var isChange = false;
                        if (kw.IndexOf("嘉義") > -1 || kw.IndexOf("朴子") > -1 || kw.IndexOf("嘉朴路") > -1) { kw = "長庚科技大學嘉義校區"; isChange = true; }
                        if (kw.IndexOf("林口") > -1 || kw.IndexOf("龜山") > -1 || kw.IndexOf("桃園") > -1 || kw.IndexOf("文化一路") > -1) { kw = "長庚科技大學林口校區"; isChange = true; }
                        if (!isChange) { kw = "長庚大學"; }
                    }
                    if (kw.IndexOf("屏東科技大學") > -1 || (kw.IndexOf("屏科大") > -1 && kw.IndexOf("店") == -1))
                    {
                        if (kw.IndexOf("獸醫") > -1)
                        {
                            kw = "屏東科技大學獸醫教學醫院";
                        }
                        else
                        {
                            kw = "屏東科技大學";
                        }
                    }
                    if ((kw.IndexOf("成功大學") > -1 || (kw.IndexOf("成大") > -1 && kw.IndexOf("校區") > -1)) && kw.IndexOf("成大醫院") == -1)
                    {
                        var isChange = false;
                        if (kw.IndexOf("歸仁") > -1 || kw.IndexOf("中正南路") > -1) { kw = "國立成功大學-歸仁校區"; isChange = true; }
                        if (kw.IndexOf("力行") > -1) { kw = "成功大學力行校區"; isChange = true; }
                        if (kw.IndexOf("敬業") > -1) { kw = "成功大學敬業校區"; isChange = true; }
                        if (kw.IndexOf("成杏") > -1 || kw.IndexOf("醫學院") > -1) { kw = "成功大學成杏校區"; isChange = true; }
                        if (kw.IndexOf("光復") > -1 && kw.IndexOf("小東路") > -1) { kw = "成功大學-光復校區-小東路出入口"; isChange = true; }
                        if (kw.IndexOf("光復") > -1 && kw.IndexOf("前鋒路") > -1) { kw = "成功大學-光復校區-前鋒路出入口"; isChange = true; }
                        if (kw.IndexOf("光復") > -1 && kw.IndexOf("勝利路") > -1) { kw = "成功大學-光復校區勝利路出入口"; isChange = true; }
                        if (!isChange && kw.IndexOf("光復") > -1) { kw = "成功大學光復校區"; isChange = true; }
                        if (kw.IndexOf("自強") > -1 && kw.IndexOf("大學路") > -1) { kw = "成功大學-自強校區大學路出入口"; isChange = true; }
                        if (kw.IndexOf("自強") > -1 && kw.IndexOf("小東路") > -1) { kw = "成功大學-自強校區小東路出入口"; isChange = true; }
                        if (kw.IndexOf("自強") > -1 && kw.IndexOf("長榮路") > -1) { kw = "成功大學-自強校區長榮路三段出入口"; isChange = true; }
                        if (!isChange && kw.IndexOf("自強") > -1) { kw = "成功大學自強校區"; isChange = true; }
                        if (kw.IndexOf("勝利校區") > -1 && kw.IndexOf("大學路") > -1) { kw = "成功大學-勝利校區大學路出入口"; isChange = true; }
                        if (kw.IndexOf("勝利校區") > -1 && kw.IndexOf("東寧路") > -1) { kw = "成功大學-勝利校區東寧路出入口"; isChange = true; }
                        if (kw.IndexOf("勝利校區") > -1 && kw.IndexOf("勝利路") > -1) { kw = "成功大學-勝利校區勝利路出入口"; isChange = true; }
                        if (!isChange && kw.IndexOf("勝利") > -1) { kw = "成功大學勝利校區"; isChange = true; }
                        if (kw.IndexOf("成功校區") > -1 && kw.IndexOf("大學路") > -1) { kw = "成功大學-成功校區大學路出入口"; isChange = true; }
                        if (kw.IndexOf("成功校區") > -1 && kw.IndexOf("小東路") > -1) { kw = "成功大學-成功校區小東路出入口"; isChange = true; }
                        if (kw.IndexOf("成功校區") > -1 && kw.IndexOf("長榮路") > -1) { kw = "成功大學-成功校區長榮路三段出入口"; isChange = true; }
                        if (kw.IndexOf("成功校區") > -1 && kw.IndexOf("勝利路") > -1) { kw = "成功大學-成功校區勝利路出入口"; isChange = true; }
                        if (!isChange) { kw = "成功大學-光復校區出入口"; }
                    }
                    if ((kw.IndexOf("德高國小") > -1 || kw.IndexOf("德高國民小學") > -1) && kw.IndexOf("台南") > -1) { kw = "台南市東區德高國小"; }
                    if ((kw.IndexOf("德高國小") > -1 || kw.IndexOf("德高國民小學") > -1) && (kw.IndexOf("台東") > -1 || kw.IndexOf("關山") > -1)) { kw = "台東縣關山鎮德高國小"; }
                    if ((kw.IndexOf("高雄") > -1 || kw.IndexOf("三民") > -1) && (kw.IndexOf("立誌") > -1 || kw.IndexOf("立志") > -1) && (kw.IndexOf("中學") > -1 || kw.IndexOf("高中") > -1)) { kw = "立志高中"; }
                    if (kw.IndexOf("民生校區") > -1 && (kw.IndexOf("東大") > -1 || (kw.IndexOf("屏東") > -1 && kw.IndexOf("大學") > -1))) { kw = "屏東大學民生校區"; }
                    if (kw.IndexOf("南港") > -1 && kw.IndexOf("公墓") > -1 && kw.IndexOf("軍") > -1) { kw = "南港軍人公墓"; }
                    if (kw.IndexOf("屏東") > -1 && kw.IndexOf("縣政府") > -1) { kw = "屏東縣政府"; }
                    if (kw.IndexOf("陶板屋") > -1 && kw.IndexOf("店") > -1 && kw.IndexOf("飯店") == -1) { kw = kw.Replace("陶板屋", "陶板屋-"); }
                    if (kw.IndexOf("墾丁路") == -1 && kw.IndexOf("墾丁") > -1 && kw.IndexOf("屏東") > -1) { kw = kw.Replace("墾丁", ""); isReplaceMarkName = true; }
                    if (kw.IndexOf("南港") > -1 && kw.IndexOf("生技園區") > -1 && kw.IndexOf("F棟") > -1) { kw = "國家生技研究園區-F棟"; }
                    if (kw.IndexOf("台東市") > -1 && kw.IndexOf("台東縣") == -1) { kw = kw.Replace("台東市", "台東縣台東市"); }
                    if (kw.IndexOf("萬華") > -1 && kw.IndexOf("馬場町") > -1) { kw = "台北市萬華區馬場町社區發展協會-"; }
                    if (kw.IndexOf("法務部矯正署") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("台中監獄") > -1 || kw.IndexOf("培德") > -1)
                        {
                            kw = "法務部矯正署台中監獄附培德醫院"; isC = true;
                        }
                        if (kw.IndexOf("監獄") > -1)
                        {
                            kw = "法務部矯正署桃園監獄"; isC = true;
                        }
                        if (!isC) { kw = "法務部矯正署"; }
                    }

                    if (kw.IndexOf("龜山龜山") > -1 && kw.IndexOf("龜山區") == -1) { kw = kw.Replace("龜山龜山", "龜山"); }
                    if (kw.IndexOf("部子國小") > -1 || (kw.IndexOf("台中") > -1 && kw.IndexOf("北屯") > -1 && kw.IndexOf("朴子國小") > -1)) { kw = "廍子國小"; }
                    if (kw.IndexOf("瑞芳") > -1 && kw.IndexOf("勸濟堂") > -1) { kw = "勸濟堂"; }
                    if (kw.IndexOf("烘爐地") > -1 && kw.IndexOf("甕缸雞") == -1) { kw = "烘爐地南山福德宮"; }
                    if (kw.IndexOf("烘爐地") > -1 && kw.IndexOf("甕缸雞") > -1) { kw = "烘爐地甕缸雞"; }
                    if (kw.IndexOf("台南市") > -1 && kw.IndexOf("衛生局") > -1 && kw.IndexOf("東興") == -1) { kw = "台南市政府衛生局"; }
                    if (kw.IndexOf("台南市") > -1 && kw.IndexOf("衛生局") > -1 && kw.IndexOf("東興") > -1) { kw = "台南市政府衛生局東興辦公室"; }
                    if (kw.IndexOf("左營火車站") > -1 && kw.IndexOf("新左營") == -1) { kw = "左營火車站-"; }
                    if (kw.IndexOf("北港朝天宮") > -1) { kw = "北港朝天宮"; }
                    if (kw.IndexOf("台中州廳") > -1) { kw = "台中州廳"; }
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("麗莎汽車旅館") > -1) { kw = "麗莎旅館"; }
                    if (kw.IndexOf("中油加油站") > -1) { kw = kw.Replace("中油加油站", "中油-"); }
                    if (kw.IndexOf("北投") > -1 && (kw.IndexOf("科大") > -1 || kw.IndexOf("科技大學") > -1) && kw.IndexOf("後門") == -1) { kw = "台北城市科大"; }
                    if (kw.IndexOf("北投") > -1 && (kw.IndexOf("科大") > -1 || kw.IndexOf("科技大學") > -1) && kw.IndexOf("後門") > -1) { kw = "台北城市科技大學後門"; }
                    if (kw.IndexOf("湖口") > -1 && kw.IndexOf("好客文創") > -1) { kw = "湖口好客文創園區"; }
                    if (kw.IndexOf("新竹") > -1 && kw.IndexOf("矽導") > -1 && kw.IndexOf("研發") > -1) { kw = "矽導竹科研發中心"; }
                    if (kw.IndexOf("永安市場站") > -1 && kw.IndexOf("捷運") == -1) { kw = kw.Replace("永安市場站", "捷運永安市場站"); }
                    if (kw.IndexOf("拱北殿") > -1 && kw.IndexOf("汐止") > -1) { kw = "汐止拱北殿"; }
                    if (kw.IndexOf("拱北殿") > -1 && kw.IndexOf("汐止") == -1) { kw = "拱北殿"; }
                    if (kw.IndexOf("市政府") > -1 && (kw.IndexOf("分局") > -1 || kw.IndexOf("派出所") > -1)) { kw = kw.Replace("市政府", ""); }

                    #region 台灣菸酒
                    if (kw.IndexOf("台灣菸酒") > -1)
                    {
                        kw = kw.Replace("台灣菸酒", "台灣菸酒-");
                    }
                    if (kw.IndexOf("台灣菸酒") == -1)
                    {
                        if (kw.IndexOf("善化") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-善化啤酒廠";
                        }
                        if (kw.IndexOf("烏日") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-烏日啤酒廠";
                        }
                        if (kw.IndexOf("南投") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-南投酒廠";
                        }
                        if (kw.IndexOf("台中") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-台中酒廠";
                        }
                        if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-嘉義酒廠";
                        }
                        if (kw.IndexOf("竹南") > -1 && kw.IndexOf("酒廠") > -1 && kw.IndexOf("製瓶") > -1)
                        {
                            kw = "台灣菸酒-竹南啤酒廠製瓶場";
                        }
                        if (kw.IndexOf("埔里") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-埔里酒廠展售中心";
                        }
                        if (kw.IndexOf("宜蘭") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-宜蘭酒廠展售中心";
                        }
                        if (kw.IndexOf("隆田") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-隆田酒廠";
                        }
                        if (kw.IndexOf("屏東") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-屏東酒廠產品推廣中心";
                        }
                        if (kw.IndexOf("竹南") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-竹南啤酒廠";
                        }
                        if (kw.IndexOf("桃園") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-桃園酒廠";
                        }
                        if (kw.IndexOf("花蓮") > -1 && kw.IndexOf("酒廠") > -1)
                        {
                            kw = "台灣菸酒-花蓮酒廠";
                        }
                    }
                    #endregion
                    #region 百貨
                    if (kw.IndexOf("信義誠品") > -1)
                    {
                        kw = "誠品-信義旗艦店";
                    }
                    if (kw.IndexOf("新光三越") > -1)
                    {
                        if (kw.IndexOf("南西店") > -1)
                        {
                            if (kw.IndexOf("三館") > -1)
                            {
                                kw = "新光三越-台北南西店三館";
                            }
                            else
                            {
                                kw = "新光三越-台北南西店一館";
                            }
                        }
                        if (kw.IndexOf("市政北七路") > -1) { kw = "新光三越-市政北七路出入口"; }
                        if (kw.IndexOf("惠來路") > -1) { kw = "新光三越-惠來路二段出入口"; }
                        if (kw.IndexOf("中港") > -1 || kw.IndexOf("台灣大道") > -1 || kw.IndexOf("台中") > -1) { kw = "新光三越-台中中港店"; }
                        if (kw.IndexOf("A11") > -1) { kw = "新光三越A11館"; }
                        if (kw.IndexOf("A4") > -1) { kw = "新光三越A4館"; }
                        if (kw.IndexOf("A8") > -1) { kw = "新光三越A8館"; }
                        if (kw.IndexOf("A9") > -1) { kw = "新光三越A9館"; }
                        if (kw.IndexOf("大有") > -1) { kw = "新光三越-桃園大有店"; }
                        if (kw.IndexOf("桃園") > -1 && kw.IndexOf("大有") == -1) { kw = "新光三越-桃園站前店"; }
                        if ((kw.IndexOf("站前") > -1 || kw.IndexOf("忠孝西路") > -1) && kw.IndexOf("桃園") == -1) { kw = "新光三越-台北站前店"; }
                        if (kw.IndexOf("天母") > -1) { kw = "新光三越-台北天母店"; }
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("新天地") > -1)
                        {
                            if (kw.IndexOf("小西門") > -1)
                            {
                                kw = "新光三越-台南新天地小西門館";
                            }
                            else if (kw.IndexOf("中山") > -1)
                            {
                                kw = "新光三越-台南中山店";
                            }
                            else
                            {
                                kw = "新光三越-台南新天地本館";
                            }
                        }
                        if (kw.IndexOf("嘉義") > -1 || kw.IndexOf("垂楊") > -1) { kw = "新光三越-嘉義垂楊店"; }
                        if (kw.IndexOf("三多") > -1) { kw = "新光三越-高雄三多店"; }
                        if (kw.IndexOf("彩虹") > -1) { kw = "新光三越-高雄左營店彩虹市集"; }
                        if ((kw.IndexOf("高雄") > -1 || kw.IndexOf("左營") > -1) && kw.IndexOf("三多") == -1 && kw.IndexOf("彩虹") == -1) { kw = "新光三越-高雄左營店"; }
                        isReplaceMarkName = true;
                    }

                    if (kw.IndexOf("SOGO") > -1)
                    {
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("台灣大道") > -1 || kw.IndexOf("廣三") > -1)
                        {
                            kw = "廣三SOGO";
                        }
                        if (kw.IndexOf("忠孝東路三段") > -1 || kw.IndexOf("復興店") > -1 || kw.IndexOf("復興館") > -1)
                        {
                            kw = "SOGO-台北復興店-復興南路出入口";
                        }
                        if (kw.IndexOf("忠孝東路四段") > -1 || kw.IndexOf("忠孝店") > -1 || kw.IndexOf("忠孝館") > -1)
                        {
                            kw = "SOGO-台北忠孝店-復興南路一段155巷出入口";
                        }
                        if (kw.IndexOf("忠孝") > -1 && kw.IndexOf("忠孝店") == -1 && kw.IndexOf("忠孝館") == -1 && kw.IndexOf("復興店") == -1 && kw.IndexOf("復興館") == -1)
                        {
                            kw = "SOGO-台北忠孝店-復興南路一段155巷出入口";
                        }
                        if (kw.IndexOf("敦化") > -1)
                        {
                            kw = "台北SOGO-敦化館";
                        }
                        if (kw.IndexOf("天母") > -1 || kw.IndexOf("遠東") > -1 || kw.IndexOf("中山北路") > -1)
                        {
                            kw = "遠東SOGO-台北天母店";
                        }
                        if (kw.IndexOf("桃園") > -1 || kw.IndexOf("中壢") > -1 || kw.IndexOf("元化路") > -1)
                        {
                            kw = "中壢SOGO-中壢店";
                        }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("前鎮") > -1 || kw.IndexOf("三多") > -1)
                        {
                            kw = "高雄SOGO-高雄店";
                        }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("CITY") > -1 || kw.IndexOf("巨城") > -1 || kw.IndexOf("中央路") > -1)
                        {
                            kw = "新竹SOGO-巨城店";
                        }
                        isReplaceMarkName = true;
                    }

                    if ((kw.IndexOf("遠百") > -1 || kw.IndexOf("遠東百貨") > -1) && kw.IndexOf("SOGO") == -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("中山路") > -1 || kw.IndexOf("遠百板橋") > -1 || kw.IndexOf("中山店") > -1) { kw = "遠百-板橋中山店"; isC = true; }
                        if (kw.IndexOf("信義") > -1 || kw.IndexOf("松仁路") > -1) { kw = "遠百信義A13"; isC = true; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("苓雅") > -1 || kw.IndexOf("三多") > -1) { kw = "高雄大遠百"; isC = true; }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("西屯") > -1 || kw.IndexOf("台灣大道") > -1) { kw = "台中大遠百"; isC = true; }
                        if (kw.IndexOf("台南") > -1 && kw.IndexOf("公園") > -1) { kw = "台南大遠百-公園店"; isC = true; }
                        if (kw.IndexOf("台南") > -1 && (kw.IndexOf("前鋒路") > -1 || kw.IndexOf("成功") > -1)) { kw = "台南大遠百-成功店"; isC = true; }
                        if (kw.IndexOf("新竹市") > -1 || kw.IndexOf("新竹大遠百") > -1 || kw.IndexOf("東區") > -1 || kw.IndexOf("西大路") > -1) { kw = "遠百-新竹大遠百店"; isC = true; }
                        if (kw.IndexOf("新竹縣") > -1 || kw.IndexOf("竹北") > -1 || kw.IndexOf("莊敬北路") > -1) { kw = "遠百竹北"; isC = true; }
                        if (kw.IndexOf("花蓮") > -1 || kw.IndexOf("和平路") > -1) { kw = "遠百花蓮"; isC = true; }
                        if (kw.IndexOf("桃園") > -1 || kw.IndexOf("中正路") > -1) { kw = "遠百桃園"; isC = true; }
                        if (kw.IndexOf("嘉義") > -1 || kw.IndexOf("垂楊路") > -1) { kw = "遠百嘉義"; isC = true; }
                        if (kw.IndexOf("寶慶") > -1 || kw.IndexOf("西門") > -1) { kw = "遠百寶慶"; isC = true; }
                        if (!isC && kw.IndexOf("大遠百") > -1) { kw = "板橋大遠百新站路大門"; }
                        isReplaceMarkName = true;
                    }

                    if ((kw.IndexOf("環球") > -1 || kw.IndexOf("GlOBALMALL") > -1) && (kw.IndexOf("購物") > -1 || kw.IndexOf("中心 ") > -1) && kw.IndexOf("捷運") == -1)
                    {
                        if (kw.IndexOf("中和") > -1) { kw = "環球購物中心-中和店"; }
                        if (kw.IndexOf("龜山") > -1 || kw.IndexOf("A8") > -1 || kw.IndexOf("復興一路") > -1) { kw = "環球購物中心-林口A8店"; }
                        if (kw.IndexOf("-") == -1 && (kw.IndexOf("林口") > -1 || kw.IndexOf("A9") > -1 || kw.IndexOf("文化三路") > -1)) { kw = "環球購物中心-林口A9店"; }
                        if (kw.IndexOf("-") == -1 && (kw.IndexOf("桃園") > -1 || kw.IndexOf("A19") > -1 || kw.IndexOf("青埔") > -1 || kw.IndexOf("中壢") > -1 || kw.IndexOf("高鐵南路") > -1)) { kw = "環球購物中心-桃園A19店"; }
                        if (kw.IndexOf("板橋") > -1 || kw.IndexOf("縣民大道") > -1) { kw = "環球購物中心-板橋車站店"; }
                        if (kw.IndexOf("南港") > -1 && kw.IndexOf("忠孝東路") > -1) { kw = "環球購物中心-南港車站店"; }
                        if (kw.IndexOf("屏東") > -1 && (kw.IndexOf("仁愛路") > -1)) { kw = "環球購物中心-屏東店"; }
                        if (kw.IndexOf("左營") > -1 || kw.IndexOf("高雄") > -1 || kw.IndexOf("站前北路") > -1) { kw = "環球購物中心-新左營車站店"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("台南") > -1 && kw.IndexOf("文創") > -1 && kw.IndexOf("園區") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("文華路") > -1 || kw.IndexOf("十鼓") > -1 || kw.IndexOf("仁糖") > -1 || kw.IndexOf("仁德") > -1) { kw = "十鼓仁糖文創園區"; isC = true; }
                        if (kw.IndexOf("西門路") > -1 || kw.IndexOf("藍晒") > -1 || kw.IndexOf("南區") > -1) { kw = "藍晒圖文創園區"; isC = true; }
                        if (!isC) { kw = "台南文創產業園區"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("微風南山") > -1 && kw.IndexOf("松廉路") > -1) { kw = "微風南山-松廉路出入口"; isReplaceMarkName = true; }
                    if (kw.IndexOf("微風南山") > -1 && kw.IndexOf("松廉路") == -1) { kw = "微風南山"; isReplaceMarkName = true; }
                    if (kw.IndexOf("大江") > -1 && kw.IndexOf("購物") > -1) { kw = "大江國際購物中心"; isReplaceMarkName = true; }
                    if (kw.IndexOf("成吉思汗") > -1 && kw.IndexOf("健身") > -1)
                    {
                        if (kw.IndexOf("三重") > -1 || kw.IndexOf("捷運路") > -1) { kw = "成吉思汗健身俱樂部-三重館"; }
                        if (kw.IndexOf("新莊") > -1 || kw.IndexOf("中原路") > -1) { kw = "成吉思汗健身俱樂部-新莊館"; }
                        if (kw.IndexOf("蘆洲") > -1 || kw.IndexOf("長安街") > -1) { kw = "成吉思汗健身俱樂部-蘆洲館"; }
                        if (kw.IndexOf("林口") > -1 || kw.IndexOf("仁愛路") > -1) { kw = "成吉思汗健身俱樂部-林口旗艦館"; }
                        if (kw.IndexOf("中壢") > -1 || kw.IndexOf("桃園") > -1 || kw.IndexOf("環西路") > -1) { kw = "成吉思汗健身俱樂部-中壢旗艦館"; }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("北屯") > -1 || kw.IndexOf("崇德二路") > -1) { kw = "成吉思汗健身俱樂部-台中旗艦館"; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("苓雅") > -1 || kw.IndexOf("忠孝二路") > -1) { kw = "成吉思汗健身俱樂部-高雄旗艦館"; }
                        isReplaceMarkName = true;
                    }
                    // 餐飲
                    if (kw.IndexOf("煙波") > -1 && (kw.IndexOf("飯店") > -1 || kw.IndexOf("館") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("永福路") > -1) { kw = "煙波大飯店-台南館"; isC = true; }
                        if (kw.IndexOf("蘇澳") > -1 || kw.IndexOf("四季雙泉") > -1) { kw = "煙波大飯店-蘇澳四季雙泉館"; isC = true; }
                        if (kw.IndexOf("宜蘭") > -1 || kw.IndexOf("凱旋路") > -1) { kw = "煙波大飯店-宜蘭館"; isC = true; }
                        if (kw.IndexOf("都會") > -1 || (kw.IndexOf("新竹") > -1 && kw.IndexOf("民生路") > -1)) { kw = "煙波大飯店-都會館"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("湖濱") > -1 || kw.IndexOf("溫莎") > -1 || kw.IndexOf("陽光") > -1) { kw = "煙波大飯店-新竹湖濱館"; isC = true; }
                        if (kw.IndexOf("山闊") > -1) { kw = "煙波大飯店-花蓮太魯閣山闊館"; isC = true; }
                        if (!isC && (kw.IndexOf("沁海") > -1 || kw.IndexOf("太魯閣") > -1)) { kw = "煙波大飯店-花蓮太魯閣沁海館"; isC = true; }
                        if (!isC && kw.IndexOf("花蓮") > -1) { kw = "煙波大飯店-花蓮館"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("來來豆漿") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("文心路") > -1) { kw = "台北內湖來來豆漿台中店"; isC = true; }
                        if (kw.IndexOf("前金") > -1 || kw.IndexOf("六合") > -1) { kw = "果貿來來豆漿-六合店"; isC = true; }
                        if (kw.IndexOf("左營") > -1 || kw.IndexOf("重愛") > -1) { kw = "果貿來來豆漿-重愛店"; isC = true; }
                        if (!isC && (kw.IndexOf("高雄") > -1 || kw.IndexOf("三民") > -1 || kw.IndexOf("九如") > -1)) { kw = "台北內湖來來豆漿連鎖"; isC = true; }
                        if (kw.IndexOf("永和來來") > -1 || kw.IndexOf("彰化") > -1 || kw.IndexOf("長順街") > -1) { kw = "永和來來豆漿大王"; isC = true; }
                        if (kw.IndexOf("桃園") > -1 || kw.IndexOf("永安路") > -1) { kw = "來來豆漿店"; isC = true; }
                        if (kw.IndexOf("成功") > -1) { kw = "成功來來豆漿店"; isC = true; }
                        if (!isC) { kw = "來來豆漿"; }
                        isReplaceMarkName = true;
                    }
                    #endregion
                    #region 影城
                    if ((kw.IndexOf("內湖") > -1 || kw.IndexOf("東湖") > -1) && kw.IndexOf("哈拉影城") > -1)
                    {
                        kw = "哈拉影城";
                    }

                    if (kw.IndexOf("威秀") > -1)
                    {
                        if (kw.IndexOf("信義") > -1 || kw.IndexOf("松壽") > -1)
                        {
                            kw = "威秀影城-台北信義";
                        }
                        if (kw.IndexOf("市民大道") > -1 || kw.IndexOf("京站") > -1)
                        {
                            kw = "威秀影城-台北京站";
                        }
                        if (kw.IndexOf("武昌") > -1 || kw.IndexOf("日新") > -1)
                        {
                            kw = "威秀影城-台北日新";
                        }
                        if (kw.IndexOf("環球") > -1 || kw.IndexOf("中和") > -1)
                        {
                            kw = "威秀影城-中和環球";
                        }
                        if (kw.IndexOf("板橋") > -1 || kw.IndexOf("大遠百") > -1 || kw.IndexOf("新站路") > -1)
                        {
                            kw = "威秀影城-板橋大遠百";
                        }
                        if (kw.IndexOf("林口") > -1 || kw.IndexOf("三井") > -1 || kw.IndexOf("OUTLET") > -1)
                        {
                            kw = "威秀影城-林口OUTLET";
                        }
                        if (kw.IndexOf("桃園") > -1 || kw.IndexOf("統領") > -1)
                        {
                            kw = "威秀影城-桃園統領";
                        }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("大魯閣") > -1 || kw.IndexOf("新時代") > -1)
                        {
                            kw = "威秀影城-台中大魯閣新時代";
                        }
                        if (kw.IndexOf("台中") > -1 && (kw.IndexOf("台灣大道") > -1 || kw.IndexOf("大遠百") > -1))
                        {
                            kw = "威秀影城-台中大遠百";
                        }
                        if (kw.IndexOf("台中") > -1 && (kw.IndexOf("TIGER") > -1 || kw.IndexOf("老虎") > -1 || kw.IndexOf("河南路") > -1))
                        {
                            kw = "威秀影城-台中老虎城";
                        }
                        if (kw.IndexOf("苗栗") > -1 || kw.IndexOf("頭份") > -1 || kw.IndexOf("尚順") > -1)
                        {
                            kw = "威秀影城-頭份尚順";
                        }
                        if (kw.IndexOf("台南") > -1)
                        {
                            kw = "威秀影城-台南FOCUS";
                        }
                        if (kw.IndexOf("台南") > -1 && (kw.IndexOf("公園") > -1 || kw.IndexOf("大遠百") > -1))
                        {
                            kw = "威秀影城-台南大遠百";
                        }
                        if (kw.IndexOf("台南") > -1 && (kw.IndexOf("南紡") > -1 || kw.IndexOf("中華東路") > -1))
                        {
                            kw = "威秀影城-南紡購物中心A1館";
                            if (kw.IndexOf("A2") > -1)
                            {
                                kw = "威秀影城-南紡購物中心A2館";
                            }
                        }
                        if (kw.IndexOf("高雄") > -1 && (kw.IndexOf("苓雅") > -1 || kw.IndexOf("大遠百") > -1))
                        {
                            kw = "威秀影城-高雄大遠百";
                        }
                        if (kw.IndexOf("新竹") > -1)
                        {
                            kw = "威秀影城-新竹大遠百";
                        }
                        if (kw.IndexOf("巨城") > -1)
                        {
                            kw = "威秀影城-新竹巨城";
                        }
                        if (kw.IndexOf("花蓮") > -1 || kw.IndexOf("新天堂") > -1)
                        {
                            kw = "威秀影城-花蓮新天堂樂園";
                        }
                    }

                    #endregion
                    #region 有固定格式的地標
                    var tempKw = "";
                    string[] markNamelist0 = { "統聯", "葛瑪蘭", "國光", "阿羅哈", "欣欣", "和欣", "日統" };
                    var getStation = kw.IndexOf("站");
                    if (markNamelist0.Any(x => kw.IndexOf(x + "客運") > -1) && getStation > -1)
                    {
                        foreach (var m in markNamelist0)
                        {
                            var k1 = kw.IndexOf(m);
                            if (k1 > -1)
                            {
                                if (k1 < getStation)
                                {
                                    var getW = kw.Substring(k1 + (m.Length + 2), getStation - k1 - (m.Length + 2));
                                    tempKw = m + "客運-" + getW + "站";
                                }
                                else
                                {
                                    var getW = kw.Substring(0, getStation);
                                    tempKw = m + "客運-" + getW + "站";
                                }
                                break;
                            }
                        }
                    }
                    if (markNamelist0.Any(x => kw.IndexOf(x + "客運") == -1 && kw.IndexOf(x) > -1) && getStation > -1)
                    {
                        foreach (var m in markNamelist0)
                        {
                            var k1 = kw.IndexOf(m);
                            if (k1 > -1)
                            {
                                if (k1 < getStation)
                                {
                                    var getW = kw.Substring(k1 + m.Length, getStation - k1 - m.Length);
                                    tempKw = m + "客運-" + getW + "站";
                                }
                                else
                                {
                                    var getW = kw.Substring(0, getStation);
                                    tempKw = m + "客運-" + getW + "站";
                                }
                                break;
                            }
                        }
                    }

                    #region **-店
                    if (kw.IndexOf("-") == -1)
                    {
                        string[] markNamelist = { "錢櫃", "全家", "好樂迪", "萊爾富", "OK", "家樂福", "大潤發", "寶雅", "全聯", "好市多", "愛買", "美廉社", "頂好Wellcome", "春水堂", "貴族世家", "小時厚牛排", "特力屋" };
                        var getStore = kw.IndexOf("店");
                        if (markNamelist.Any(x => kw.IndexOf(x) > -1) && getStore > -1)
                        {
                            var ckStore = kw.Split("店");
                            foreach (var m in markNamelist)
                            {
                                var k1 = kw.IndexOf(m);
                                if (k1 > -1)
                                {
                                    if (ckStore.Length > 2)
                                    {
                                        // 如果有超過兩個店名，取最後一個
                                        var kw1 = ckStore[^2] + "店";
                                        if (kw1.IndexOf(m) > -1)
                                        {
                                            k1 = kw1.IndexOf(m);
                                            getStore = kw1.IndexOf("店");
                                            if (k1 < getStore)
                                            {
                                                var getW = kw1.Substring(k1 + m.Length, getStore - k1 - m.Length);
                                                tempKw = m + "-" + getW + "店";
                                            }
                                            else
                                            {
                                                var getW = kw1.Substring(0, getStore);
                                                tempKw = m + "-" + getW + "店";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (k1 < getStore)
                                        {
                                            var getW = kw.Substring(k1 + m.Length, getStore - k1 - m.Length);
                                            tempKw = m + "-" + getW + "店";
                                        }
                                        else
                                        {
                                            var getW = kw.Substring(0, getStore);
                                            tempKw = m + "-" + getW + "店";
                                        }
                                    }
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(tempKw))
                        {
                            if (tempKw == "統聯客運-三重站") { tempKw = "統聯客運-重陽站"; }
                            tempKw = tempKw.Replace("全聯中心", "");

                            var getMarkName = await GoASRAPI(tempKw, "", markName: tempKw).ConfigureAwait(false);
                            resAddr.Address = kw;
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            getMarkName = await GoASRAPI(tempKw.Split("-")[0] + "-", "", markName: tempKw.Split("-")[1]).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            var tempKw2 = tempKw.Split("-")[1].Replace("站", "").Replace("店", "");
                            if (!string.IsNullOrEmpty(tempKw2))
                            {
                                var thesaurus = " FORMSOF(THESAURUS," + tempKw.Split("-")[0] + ") and FORMSOF(THESAURUS," + tempKw2 + ") ";
                                getMarkName = await GoASRAPI("", thesaurus).ConfigureAwait(false);
                                resAddr.Address = kw;
                                if (getMarkName != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    #endregion

                    #region **-門市
                    if (kw.IndexOf("-") == -1)
                    {
                        string[] markNamelist = { "麥當勞", "星巴克", "康是美" };
                        var getStore = kw.IndexOf("門市");
                        if (markNamelist.Any(x => kw.IndexOf(x) > -1) && getStore > -1)
                        {
                            var ckStore = kw.Split("門市");
                            foreach (var m in markNamelist)
                            {
                                var k1 = kw.IndexOf(m);
                                if (k1 > -1)
                                {
                                    if (ckStore.Length > 2)
                                    {
                                        // 如果有超過兩個店名，取最後一個
                                        var kw1 = ckStore[^2] + "門市";
                                        if (kw1.IndexOf(m) > -1)
                                        {
                                            k1 = kw1.IndexOf(m);
                                            getStore = kw1.IndexOf("門市");
                                            if (k1 < getStore)
                                            {
                                                var getW = kw1.Substring(k1 + m.Length, getStore - k1 - m.Length);
                                                tempKw = m + "-" + getW + "門市";
                                            }
                                            else
                                            {
                                                var getW = kw1.Substring(0, getStore);
                                                tempKw = m + "-" + getW + "門市";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (k1 < getStore)
                                        {
                                            var getW = kw.Substring(k1 + m.Length, getStore - k1 - m.Length);
                                            tempKw = m + "-" + getW + "門市";
                                        }
                                        else
                                        {
                                            var getW = kw.Substring(0, getStore);
                                            tempKw = m + "-" + getW + "門市";
                                        }
                                    }
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(tempKw))
                        {
                            var getMarkName = await GoASRAPI(tempKw, "", markName: tempKw).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            getMarkName = await GoASRAPI(tempKw.Split("-")[0] + "-", "", markName: tempKw.Split("-")[1]).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    #endregion

                    #region 好市多
                    if ((kw.IndexOf("好市多") > -1 || kw.IndexOf("COSTCO") > -1) && kw.IndexOf("-") == -1)
                    {
                        if (kw.IndexOf("中和") > -1)
                        {
                            kw = "好市多-中和店";
                        }
                        if (kw.IndexOf("新莊") > -1 || kw.IndexOf("建國一路") > -1)
                        {
                            kw = "好市多-新莊店";
                        }
                        if ((kw.IndexOf("內湖") > -1 || kw.IndexOf("舊宗路") > -1) && kw.IndexOf("停車") > -1)
                        {
                            kw = "好市多-內湖店停車場";
                        }
                        if ((kw.IndexOf("內湖") > -1 || kw.IndexOf("舊宗路") > -1) && kw.IndexOf("停車場") == -1)
                        {
                            kw = "好市多-內湖店";
                        }
                        if (kw.IndexOf("汐止") > -1 || kw.IndexOf("大同路") > -1)
                        {
                            kw = "好市多-汐止店";
                        }
                        if (kw.IndexOf("北投") > -1 || kw.IndexOf("立德路") > -1)
                        {
                            kw = "好市多-北投店";
                        }
                        if (kw.IndexOf("中壢") > -1 || kw.IndexOf("民族路") > -1)
                        {
                            kw = "好市多-桃園中壢店";
                        }
                        if (kw.IndexOf("南崁") > -1 || kw.IndexOf("蘆竹") > -1)
                        {
                            kw = "好市多-桃園南崁店";
                        }
                        if (kw.IndexOf("北屯") > -1 || kw.IndexOf("敦富路") > -1)
                        {
                            kw = "好市多-北台中店";
                        }
                        if ((kw.IndexOf("台中") > -1 || kw.IndexOf("南屯") > -1 || kw.IndexOf("文心南三路") > -1) && kw.IndexOf("北台中") == -1)
                        {
                            kw = "好市多-台中店";
                        }
                        if (kw.IndexOf("鼓山") > -1 || kw.IndexOf("大順") > -1)
                        {
                            kw = "好市多-北高雄大順店";
                        }
                        if ((kw.IndexOf("高雄") > -1 || kw.IndexOf("前鎮") > -1 || kw.IndexOf("中華五路") > -1) && kw.IndexOf("大順") == -1)
                        {
                            kw = "好市多-高雄店";
                        }
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("和緯路") > -1)
                        {
                            kw = "好市多-台南店";
                        }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("慈雲路") > -1)
                        {
                            kw = "好市多-新竹店";
                        }
                        if (kw.IndexOf("嘉義") > -1 || kw.IndexOf("忠孝路") > -1)
                        {
                            kw = "好市多-嘉義店";
                        }
                    }
                    #endregion

                    #region 迪卡儂
                    if (kw.IndexOf("迪卡儂") > -1)
                    {
                        if (kw.IndexOf("北屯") > -1 || kw.IndexOf("中清路") > -1) { kw = "迪卡儂-台中北屯店"; }
                        if (kw.IndexOf("南屯") > -1 || kw.IndexOf("大墩南路") > -1) { kw = "迪卡儂-台中南屯店"; }
                        if (kw.IndexOf("大同區") > -1 || kw.IndexOf("中山店") > -1 || kw.IndexOf("南京西路") > -1) { kw = "迪卡儂-台北中山店"; }
                        if (kw.IndexOf("內湖") > -1 || kw.IndexOf("新湖一路") > -1) { kw = "迪卡儂-台北內湖店"; }
                        if (kw.IndexOf("桂林") > -1 || kw.IndexOf("萬華") > -1) { kw = "迪卡儂-台北桂林店"; }
                        if (kw.IndexOf("台東") > -1) { kw = "迪卡儂-台東店"; }
                        if (kw.IndexOf("屏東") > -1 || kw.IndexOf("自由路") > -1) { kw = "迪卡儂-屏東店"; }
                        if (kw.IndexOf("雲林") > -1 || kw.IndexOf("斗六") > -1) { kw = "迪卡儂-雲林店"; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("慈雲路") > -1) { kw = "迪卡儂-新竹店"; }
                        if (kw.IndexOf("嘉義") > -1 || kw.IndexOf("博愛路") > -1) { kw = "迪卡儂-嘉義店"; }
                        if (kw.IndexOf("仁德") > -1 || kw.IndexOf("中山路") > -1) { kw = "迪卡儂-台南仁德店"; }
                        if (kw.IndexOf("台南西門") > -1 || kw.IndexOf("臨安路") > -1) { kw = "迪卡儂-台南西門店"; }
                        if (kw.IndexOf("八德") > -1 || kw.IndexOf("介壽路") > -1) { kw = "迪卡儂-桃園八德店"; }
                        if (kw.IndexOf("中壢") > -1 || kw.IndexOf("中華路") > -1) { kw = "迪卡儂-桃園中壢店"; }
                        if (kw.IndexOf("亞灣") > -1 || kw.IndexOf("前鎮") > -1 || kw.IndexOf("時代大道") > -1) { kw = "迪卡儂-高雄亞灣店"; }
                        if (kw.IndexOf("楠梓") > -1 || kw.IndexOf("土庫一路") > -1) { kw = "迪卡儂-高雄楠梓店"; }
                        if (kw.IndexOf("鳳山") > -1 || kw.IndexOf("文化路") > -1) { kw = "迪卡儂-高雄鳳山店"; }
                        if (kw.IndexOf("三重") > -1 || kw.IndexOf("集美街") > -1) { kw = "迪卡儂-新北三重店"; }
                        if (kw.IndexOf("中和") > -1 || kw.IndexOf("中山路") > -1) { kw = "迪卡儂-新北中和店"; }
                        if (kw.IndexOf("新店") > -1 || kw.IndexOf("中興路") > -1) { kw = "迪卡儂-新北新店店"; }
                    }
                    #endregion

                    #region 愛買
                    if (kw.IndexOf("愛買") > -1)
                    {
                        if (kw.IndexOf("忠孝") > -1 || (kw.IndexOf("信義") > -1 && kw.IndexOf("基隆") == -1)) { kw = "愛買-忠孝店"; }
                        if (kw.IndexOf("文山") > -1 || kw.IndexOf("景美") > -1) { kw = "愛買-景美店"; }
                        if (kw.IndexOf("基隆") > -1 || kw.IndexOf("深溪") > -1) { kw = "愛買-基隆店"; }
                        if (kw.IndexOf("桃園") > -1 && kw.IndexOf("楊梅") == -1) { kw = "愛買-桃園店"; }
                        if (kw.IndexOf("楊梅") > -1) { kw = "愛買-楊梅店"; }
                        if (kw.IndexOf("三重") > -1) { kw = "愛買--三重店"; }
                        if (kw.IndexOf("永和") > -1) { kw = "愛買-永和店"; }
                        if (kw.IndexOf("板橋") > -1 || kw.IndexOf("南雅") > -1) { kw = "愛買-南雅店"; }
                        if (kw.IndexOf("中港") > -1 || kw.IndexOf("台灣大道") > -1) { kw = "愛買-中港店"; }
                        if (kw.IndexOf("水湳") > -1 || kw.IndexOf("中清路") > -1) { kw = "愛買-水湳店"; }
                        if (kw.IndexOf("永福") > -1 || kw.IndexOf("福科路") > -1) { kw = "愛買-永福店"; }
                        if (kw.IndexOf("南區") > -1 || kw.IndexOf("復興路") > -1) { kw = "愛買-復興店"; }
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("永康") > -1) { kw = "愛買-台南店"; }
                        if (kw.IndexOf("巨城") > -1 || kw.IndexOf("中央路") > -1) { kw = "愛買-巨城店"; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("公道五路") > -1) { kw = "愛買-新竹店"; }
                        if (kw.IndexOf("花蓮") > -1 || kw.IndexOf("和平路") > -1) { kw = "愛買-花蓮店"; }
                        if (kw.IndexOf("豐原") > -1 || kw.IndexOf("水源路") > -1) { kw = "愛買-豐原店"; }
                    }
                    #endregion

                    #region 家樂福 (未完全)
                    if (kw.IndexOf("家樂福") > -1 && kw.IndexOf("-") == -1)
                    {
                        if (kw.IndexOf("內壢") > -1) { kw = "家樂福-內壢店"; }
                        if (kw.IndexOf("經國") > -1) { kw = "家樂福-經國店"; }
                        if (kw.IndexOf("中原") > -1) { kw = "家樂福-中原店"; }
                        if (kw.IndexOf("中壢") > -1) { kw = "家樂福-中壢店"; }
                        if (kw.IndexOf("土城") > -1) { kw = "家樂福-土城店"; }
                        if (kw.IndexOf("大墩") > -1) { kw = "家樂福-大墩店"; }
                        if (kw.IndexOf("中平") > -1) { kw = "家樂福-中平店"; }
                        if (kw.IndexOf("八德") > -1) { kw = "家樂福-八德店"; }
                        if (kw.IndexOf("三民") > -1) { kw = "家樂福-三民店"; }
                        if (kw.IndexOf("大直") > -1) { kw = "家樂福-大直店"; }
                        if (kw.IndexOf("汐科") > -1) { kw = "家樂福-汐科店"; }
                        if (kw.IndexOf("重新") > -1) { kw = "家樂福-重新店"; }
                        if (kw.IndexOf("淡新") > -1) { kw = "家樂福-淡新店"; }
                        if (kw.IndexOf("新店") > -1) { kw = "家樂福-新店店"; }
                        if (kw.IndexOf("樹林") > -1) { kw = "家樂福-樹林店"; }
                        if (kw.IndexOf("仁德") > -1) { kw = "家樂福-仁德店"; }
                        if (kw.IndexOf("豐原") > -1) { kw = "家樂福-豐原店"; }
                        if (kw.IndexOf("彰化") > -1) { kw = "家樂福-彰化店"; }
                        if (kw.IndexOf("青海") > -1) { kw = "家樂福-青海店"; }
                        if (kw.IndexOf("安平") > -1) { kw = "家樂福-安平店"; }
                        if (kw.IndexOf("文心") > -1) { kw = "家樂福-文心店"; }
                        if (kw.IndexOf("蘆洲") > -1 || kw.IndexOf("五華街") > -1) { kw = "家樂福-蘆洲店"; }
                        if (kw.IndexOf("鬥六家樂福") > -1 || kw.IndexOf("斗六家樂福") > -1) { kw = "家樂福-斗六店"; }
                    }
                    #endregion

                    #region 楓康超市 (未完全)
                    if (kw.IndexOf("楓康超市") > -1)
                    {
                        if (kw.IndexOf("東興") > -1)
                        {
                            kw = "楓康超市-東興店";
                        }
                        if (kw.IndexOf("大昌") > -1 || kw.IndexOf("大墩十街") > -1)
                        {
                            kw = "台中市西區大墩十街92號";
                        }
                        if (kw.IndexOf("東興") > -1)
                        {
                            kw = "楓康超市-東興店";
                        }
                        if (kw.IndexOf("三民") > -1)
                        {
                            kw = "楓康超市-三民店";
                        }
                    }
                    #endregion

                    #region 匯豐汽車 (未完全)
                    if (kw.IndexOf("匯豐汽車") > -1)
                    {
                        if (kw.IndexOf("龍潭") > -1) { kw = "匯豐汽車-龍潭維修廠"; }
                        if (kw.IndexOf("三重") > -1 || kw.IndexOf("國道路") > -1) { kw = "匯豐汽車-三重維修廠"; }
                        if (kw.IndexOf("三鶯") > -1 || kw.IndexOf("三峽") > -1) { kw = "匯豐汽車-三鶯維修廠"; }
                        if (kw.IndexOf("土城") > -1) { kw = "匯豐汽車-土城維修廠"; }
                        if (kw.IndexOf("大甲") > -1) { kw = "匯豐汽車-大甲維修廠"; }
                        if (kw.IndexOf("大里") > -1) { kw = "匯豐汽車-大里維修廠"; }
                        if (kw.IndexOf("小港") > -1) { kw = "匯豐汽車-小港維修廠"; }
                        if (kw.IndexOf("中和") > -1) { kw = "匯豐汽車-中和維修廠"; }
                        if (kw.IndexOf("中區") > -1) { kw = "匯豐汽車-中區區域部"; }
                        if (kw.IndexOf("中華") > -1 && kw.IndexOf("中華路") == -1) { kw = "匯豐汽車-中華維修廠"; }
                        if (kw.IndexOf("中壢") > -1 || kw.IndexOf("西園路") > -1) { kw = "匯豐汽車-中壢維修廠"; }
                        if (kw.IndexOf("五甲") > -1 || kw.IndexOf("瑞隆東路") > -1) { kw = "匯豐汽車-五甲維修廠"; }
                        if (kw.IndexOf("五權") > -1 && kw.IndexOf("維修") == -1) { kw = "匯豐汽車-五權展示中心"; }
                        if (kw.IndexOf("五權") > -1 && kw.IndexOf("維修") > -1) { kw = "匯豐汽車-五權維修廠"; }
                        if (kw.IndexOf("仁德") > -1) { kw = "匯豐汽車-仁德維修廠"; }
                        if (kw.IndexOf("文心") > -1) { kw = "匯豐汽車-文心維修廠"; }
                        if (kw.IndexOf("斗南") > -1 || kw.IndexOf("大業路") > -1) { kw = "匯豐汽車-斗南維修廠"; }
                    }
                    #endregion

                    #region 特力屋 (未完全)
                    if (kw.IndexOf("特力屋") > -1 && kw.IndexOf("-") == -1)
                    {
                        if (kw.IndexOf("南崁") > -1 || kw.IndexOf("蘆竹") > -1) { kw = "特力屋-桃園南崁店"; }
                        if (kw.IndexOf("八德") > -1 || kw.IndexOf("介壽路") > -1) { kw = "特力屋-八德店"; }
                        if (kw.IndexOf("中壢") > -1 || kw.IndexOf("龍岡") > -1) { kw = "特力屋-中壢龍岡店"; }
                        if (kw.IndexOf("平鎮") > -1 || kw.IndexOf("環南路") > -1) { kw = "特力屋-平鎮店"; }
                        if (kw.IndexOf("大業") > -1) { kw = "特力屋-桃園大業社區店"; }
                        if (kw.IndexOf("龍潭") > -1 || kw.IndexOf("北龍") > -1) { kw = "特力屋-龍潭北龍社區店"; }
                        if (kw.IndexOf("士林") > -1 || kw.IndexOf("基河路") > -1) { kw = "特力屋-士林店"; }
                        if (kw.IndexOf("大同") > -1 || kw.IndexOf("重慶北") > -1) { kw = "特力屋-大同重慶北店"; }
                        if (kw.IndexOf("大安") > -1 || kw.IndexOf("安和") > -1) { kw = "特力屋-大安安和社區店"; }
                        if (kw.IndexOf("內湖") > -1 || kw.IndexOf("新湖三路") > -1) { kw = "特力屋-內湖店"; }
                        if (kw.IndexOf("興華") > -1 || kw.IndexOf("南港") > -1) { kw = "特力屋-南港興華店"; }
                        if (kw.IndexOf("三重") > -1 || kw.IndexOf("集美") > -1) { kw = "特力屋-三重集美社區店"; }
                        if (kw.IndexOf("土城") > -1 || kw.IndexOf("青雲路") > -1) { kw = "特力屋-土城店"; }
                        if (kw.IndexOf("中和") > -1) { kw = "特力屋-中和店"; }
                        if (kw.IndexOf("永和") > -1 || kw.IndexOf("得和") > -1) { kw = "特力屋-永和得和社區店"; }
                        if (kw.IndexOf("林口") > -1 || kw.IndexOf("中山社區") > -1) { kw = "特力屋-林口中山社區店"; }
                        if (kw.IndexOf("板橋") > -1 || kw.IndexOf("新埔") > -1) { kw = "特力屋-板橋新埔社區店"; }
                        if (kw.IndexOf("新店") > -1 || kw.IndexOf("中興路") > -1) { kw = "特力屋-新店店"; }
                        if (kw.IndexOf("新莊") > -1) { kw = "特力屋-新莊店"; }
                        if (kw.IndexOf("三峽") > -1 || kw.IndexOf("樹林") > -1) { kw = "特力屋-台北三峽店"; }
                        if (kw.IndexOf("長安社區") > -1 || kw.IndexOf("長安街") > -1) { kw = "特力屋-蘆洲長安社區店"; }
                        if (kw.IndexOf("集賢社區") > -1 || kw.IndexOf("集賢路") > -1) { kw = "特力屋-蘆洲集賢社區店"; }
                    }
                    #endregion

                    if (kw.EndsWith("全家便利商店"))
                    {
                        kw = "全家-" + kw.Replace("全家便利商店", "");
                        if (kw.IndexOf("五結") > -1 && kw.IndexOf("宜蘭") > -1) { kw = kw.Replace("宜蘭", ""); }
                    }
                    if (kw.IndexOf("全家便利商店") > -1 && kw.IndexOf("-") == -1) { kw = kw.Replace("全家便利商店", "全家-"); }
                    if (kw.IndexOf("內湖") > -1 && kw.IndexOf("好樂迪") > -1 && kw.IndexOf("店") == -1) { kw = "好樂迪-台北內湖店"; }
                    #endregion
                }
                #endregion

                #region 特殊區域型地標 (可能有號)
                if (checkMarkName && !isCrossRoadKW && kw.IndexOf("-") == -1)
                {
                    if ((kw.IndexOf("台中洲際棒球場") > -1 || kw.IndexOf("洲際棒球場") > -1) && kw != "台中洲際棒球場")
                    {
                        kw = "台中洲際棒球場"; isReplaceMarkName = true;
                    }
                    if ((kw.IndexOf("台北和平籃球館") > -1 || kw.IndexOf("和平籃球館") > -1) && kw != "台北和平籃球館")
                    {
                        kw = "台北和平籃球館-計程車搭車處"; isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("樓") > 0 && kw.IndexOf("號") == -1)
                    {
                        // 樓的前面不是數字就不切
                        var getFloor = kw.Substring(kw.IndexOf("樓") - 1, 1);
                        if (reg1.IsMatch(getFloor))
                        {
                            kw = kw.Substring(0, kw.IndexOf("樓") - 1);
                            isReplaceMarkName = true;
                        }
                    }
                    if (kw.IndexOf("和平藝術") > -1 && (kw.IndexOf("台北") > -1 || kw.IndexOf("信義") > -1))
                    {
                        kw = kw.Replace("和平藝術", "和平藝墅");
                    }
                    if (kw.IndexOf("和平藝墅") > -1)
                    {
                        kw = "和平藝墅";
                    }
                    if (kw.IndexOf("台北小城") > -1 && kw.IndexOf("僑愛七路") > -1 && kw.IndexOf("號") > -1)
                    {
                        kw = kw.Replace("台北小城", "");
                    }
                    if (kw.IndexOf("台北市") > -1 && kw.IndexOf("東區") > -1 && kw.IndexOf("號") > -1)
                    {
                        kw = kw.Replace("東區", "");
                    }
                    if (kw.IndexOf("大鵬華城") > -1)
                    {
                        kw = "大鵬華城";
                    }
                    if (checkNum > -1 && kw.IndexOf("沙鹿區公所") > -1 && kw.IndexOf("鎮政路") > -1) { kw = kw.Replace("沙鹿區公所", "沙鹿區"); }
                    if (kw.IndexOf("台東") > -1 && kw.IndexOf("中興") > -1 && kw.IndexOf("高爾夫") > -1) { kw = "台糖中興GOLF練習場"; }
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("三民") > -1 && kw.IndexOf("殯儀館") > -1 && kw.IndexOf("大華") == -1) { kw = "高雄市第一殯儀館"; }
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("殯儀") > -1 && kw.IndexOf("大華館") > -1) { kw = "高雄市第一殯儀館-大華館"; }
                    if (kw.IndexOf("高雄") > -1 && kw.IndexOf("殯儀") > -1 && kw.IndexOf("二館") > -1) { kw = "高雄市第一殯儀館-大華二館"; }
                    if (kw.IndexOf("桃園") > -1 && kw.IndexOf("殯儀館") > -1) { kw = "桃園市政府殯葬管理所"; }
                    if (kw.IndexOf("台中市立") > -1 && kw.IndexOf("殯儀館") > -1) { kw = "崇德殯儀館"; }
                    if (kw.IndexOf("台南市") > -1 && kw.IndexOf("殯儀館") > -1) { kw = "台南市南區殯儀館"; }
                    if (kw.IndexOf("光復北台北") > -1) { kw = kw.Replace("光復北台北", "台北"); }
                    if ((kw.IndexOf("地方法院") > -1 || kw.IndexOf("簡易法庭") > -1 || kw.IndexOf("簡易庭") > -1) && kw.IndexOf("宜蘭") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("中山路") > -1 || kw.IndexOf("第二") > -1) { kw = "宜蘭地方法院第二辦公室"; isC = true; }
                        if (kw.IndexOf("羅東") > -1 || kw.IndexOf("五結") > -1 || kw.IndexOf("仁愛路") > -1) { kw = "宜蘭地方法院羅東簡易庭"; isC = true; }
                        if (!isC) { kw = "台灣宜蘭地方法院"; }
                    }
                    if (kw.IndexOf("六窟") > -1 && kw.IndexOf("溫泉") > -1) { kw = "六窟溫泉餐廳"; }
                    if (kw.IndexOf("台南") > -1 && kw.IndexOf("地方法院") > -1) { kw = "台灣台南地方法院"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十二") > -1 || kw.IndexOf("12") > -1) && kw.IndexOf("P1") > -1 && kw.IndexOf("圓環") == -1) { kw = "台積電-晶圓十二廠P1"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十二") > -1 || kw.IndexOf("12") > -1) && kw.IndexOf("P1") > -1 && kw.IndexOf("圓環") > -1) { kw = "台積電十二廠P1小廳圓環"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十二") > -1 || kw.IndexOf("12") > -1) && kw.IndexOf("P7") > -1 && kw.IndexOf("機車") > -1) { kw = "P7機車棚"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十二") > -1 || kw.IndexOf("12") > -1) && kw.IndexOf("P7") > -1) { kw = "台積電十二廠P7"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("二五廠") > -1 || kw.IndexOf("25廠") > -1)) { kw = "台積電二五廠"; }
                    if (kw.IndexOf("台積") > -1 && kw.IndexOf("15A") > -1) { kw = "台積電15A"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十四") > -1 || kw.IndexOf("14廠") > -1)) { kw = "台積電-晶圓十四廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("八廠") > -1 || kw.IndexOf("8廠") > -1)) { kw = "台積電-晶圓八廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("二廠") > -1 || kw.IndexOf("2廠") > -1)) { kw = "台積電-晶圓二廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("三廠") > -1 || kw.IndexOf("3廠") > -1)) { kw = "台積電-晶圓三廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("五廠") > -1 || kw.IndexOf("5廠") > -1)) { kw = "台積電-晶圓五廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("十五廠") > -1 || kw.IndexOf("15廠") > -1)) { kw = "台積電-晶圓十五廠"; }
                    if (kw.IndexOf("台積") > -1 && (kw.IndexOf("七廠") > -1 || kw.IndexOf("7廠") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("興業一路") > -1) { kw = "台積電7廠-興業一路出入口"; isC = true; }
                        if (kw.IndexOf("運動館") > -1) { kw = "台積電七廠運動館"; isC = true; }
                        if (kw.IndexOf("學習中心") > -1) { kw = "台積電七廠學習中心"; isC = true; }
                        if (!isC) { kw = "台積電7廠-研新二路出入口"; }
                    }

                    #region 娛樂
                    if (kw.IndexOf("星聚點") > -1 && kw.IndexOf("鐵板燒") == -1)
                    {
                        if (kw.IndexOf("旗艦") > -1 || kw.IndexOf("延平") > -1) { kw = "星聚點KTV-台北旗艦館"; }
                        if (kw.IndexOf("板橋") > -1) { kw = "星聚點KTV-板橋館"; }
                        if (kw.IndexOf("復興") > -1) { kw = "星聚點KTV-復興館"; }
                        if (kw.IndexOf("西門") > -1 || kw.IndexOf("成都路") > -1) { kw = "星聚點KTV-西門館"; }
                        if (kw.IndexOf("歡唱吧") > -1 || kw.IndexOf("高雄") > -1 || kw.IndexOf("鳳山") > -1) { kw = "星聚點歡唱吧"; }
                    }
                    if (kw.IndexOf("銀櫃") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("旗艦") > -1) { kw = "銀櫃KTV-旗艦店"; isC = true; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("大昌二路") > -1) { kw = "銀櫃KTV-高雄店"; isC = true; }
                        if (kw.IndexOf("梧棲") > -1) { kw = "銀櫃KTV-梧棲店"; isC = true; }
                        if (kw.IndexOf("逢甲") > -1 || kw.IndexOf("西屯路") > -1) { kw = "銀櫃KTV-逢甲店"; isC = true; }
                        if (kw.IndexOf("一廣") > -1 || kw.IndexOf("中區") > -1) { kw = "銀櫃KTV-一廣店"; isC = true; }
                        if (kw.IndexOf("員林") > -1 || kw.IndexOf("彰化") > -1) { kw = "銀櫃自助式KTV-員林店"; isC = true; }
                        if (!isC) { kw = "銀櫃KTV-旗艦店"; }
                    }
                    if (kw.IndexOf("享溫馨") > -1 && kw.IndexOf("旅行社") == -1 && kw.IndexOf("會館") == -1 && kw.IndexOf("建設") == -1)
                    {
                        if (kw.IndexOf("仁武") > -1 || kw.IndexOf("京吉三路") > -1) { kw = "享溫馨KTV-高雄仁武二代店"; }
                        if (kw.IndexOf("澎湖") > -1 || kw.IndexOf("馬公") > -1) { kw = "享溫馨KTV-澎湖店"; }
                        if (kw.IndexOf("安平") > -1 || kw.IndexOf("慶平路") > -1) { kw = "享溫馨KTV-台南安平店"; }
                        if (kw.IndexOf("中華") > -1) { kw = "享溫馨KTV-台南中華店"; }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("西屯") > -1 || kw.IndexOf("青海南街") > -1) { kw = "享溫馨KTV-台中店"; }
                        if (kw.IndexOf("台東") > -1 || kw.IndexOf("中正路") > -1) { kw = "享溫馨KTV-台東店"; }
                        if (kw.IndexOf("花蓮") > -1 || kw.IndexOf("國聯三路") > -1) { kw = "享溫馨KTV-花蓮店"; }
                        if (kw.IndexOf("屏東") > -1 || kw.IndexOf("北平路") > -1) { kw = "享溫馨KTV-屏東店"; }
                        if (kw.IndexOf("岡山") > -1 || kw.IndexOf("捷安路") > -1) { kw = "享溫馨KTV-高雄岡山店"; }
                        if (kw.IndexOf("大寮") > -1 || kw.IndexOf("捷西路") > -1) { kw = "享溫馨KTV-高雄大寮店"; }
                        if (kw.IndexOf("五甲") > -1 || kw.IndexOf("鳳山") > -1 || kw.IndexOf("鳳南路") > -1) { kw = "享溫馨KTV-高雄五甲店"; }
                        if (kw.IndexOf("五福") > -1 || kw.IndexOf("新興") > -1) { kw = "享溫馨KTV-高雄五福店"; }
                        if (kw.IndexOf("巨蛋") > -1 || kw.IndexOf("鼓山") > -1) { kw = "享溫馨KTV-高雄巨蛋店"; }
                        if (kw.IndexOf("博愛") > -1 || kw.IndexOf("左營") > -1) { kw = "享溫馨KTV-高雄博愛店"; }
                        if (kw.IndexOf("建國") > -1 || kw.IndexOf("苓雅") > -1) { kw = "享溫馨KTV-高雄新建國店"; }
                    }
                    if (kw.IndexOf("板樹體育館") > -1) { kw = "板樹體育館"; }
                    if (kw.IndexOf("屏東火車站") > -1) { kw = "屏東火車站"; }
                    if (kw.IndexOf("台北流行音樂中心") > -1 || (kw.IndexOf("台北") > -1 && kw.IndexOf("流行") > -1 && kw.IndexOf("中心") > -1 && kw.IndexOf("樂") > -1))
                    {
                        if (kw.IndexOf("文化館") > -1) { kw = "台北流行音樂中心文化館"; } else { kw = "台北流行音樂中心"; }
                    }
                    if (kw.IndexOf("太平老街") > -1 && (kw.IndexOf("雲林") > -1 || kw.IndexOf("斗六") > -1)) { kw = "雲林縣太平老街"; }
                    if (kw.IndexOf("太平老街") > -1 && kw.IndexOf("雲林") == -1 && kw.IndexOf("斗六") == -1) { kw = "嘉義縣太平老街"; }
                    if (kw.IndexOf("駁二") > -1 && kw.IndexOf("特區") > -1) { kw = "駁二藝術特區"; }
                    if (kw.IndexOf("舊路里") > -1 && kw.IndexOf("大埔") == -1 && kw.IndexOf("東舊路坑") == -1 && kw.IndexOf("振興路") == -1) { kw = kw.Replace("舊路里", ""); }
                    if (kw.IndexOf("台中區") > -1 && kw.IndexOf("監理所") == -1 && kw.IndexOf("農改場") == -1) { kw = kw.Replace("台中區", ""); }

                    #endregion
                    #region 交通工具站點
                    #region 桃園捷運
                    if (kw.IndexOf("A1站") > -1 || kw.IndexOf("A1台北車站") > -1 || kw.IndexOf("桃園捷運台北車站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("台北車站") > -1))
                    {
                        kw = "桃園捷運台北車站";
                    }
                    if (kw.IndexOf("A2站") > -1 || kw.IndexOf("A2三重站") > -1 || kw.IndexOf("捷運三重站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("三重") > -1))
                    {
                        kw = "捷運三重站";
                    }
                    if (kw.IndexOf("A3站") > -1 || kw.IndexOf("A3新北產業") > -1 || kw.IndexOf("捷運新北產業園區站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && (kw.IndexOf("新北產業") > -1 || kw.IndexOf("新產") > -1)))
                    {
                        kw = "捷運新北產業園區站";
                    }
                    if (kw.IndexOf("A4站") > -1 || kw.IndexOf("A4新莊副都心") > -1 || kw.IndexOf("捷運新莊副都心站") > -1 || kw.IndexOf("A4副都心") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("副都心") > -1))
                    {
                        kw = "捷運新莊副都心站";
                    }
                    if (kw.IndexOf("A5站") > -1 || kw.IndexOf("A5泰山") > -1 || kw.IndexOf("捷運泰山站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("泰山") > -1 && kw.IndexOf("貴和") == -1))
                    {
                        kw = "捷運泰山站";
                    }
                    if (kw.IndexOf("A6站") > -1 || kw.IndexOf("A6泰山貴和") > -1 || kw.IndexOf("捷運泰山貴和站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("貴和") > -1))
                    {
                        kw = "捷運泰山貴和站";
                    }
                    if (kw.IndexOf("A7站") > -1 || kw.IndexOf("A7體育大學") > -1 || kw.IndexOf("捷運體育大學站") > -1 || (kw.IndexOf("A7體大") > -1 && (kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1)))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("A7", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運體育大學站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運體育大學站";
                        }
                    }
                    if (kw.IndexOf("A8站") > -1 || kw.IndexOf("A8長庚醫院") > -1 || kw.IndexOf("捷運長庚醫院站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("長庚") > -1))
                    {
                        kw = "捷運長庚醫院站";
                    }
                    if (kw.IndexOf("A9站") > -1 || kw.IndexOf("A9林口") > -1 || kw.IndexOf("捷運林口站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("林口") > -1))
                    {
                        kw = "捷運林口站";
                    }
                    if (kw.IndexOf("A10站") > -1 || kw.IndexOf("A10山鼻") > -1 || kw.IndexOf("捷運山鼻站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("山鼻") > -1))
                    {
                        kw = "捷運山鼻站";
                    }
                    if (kw.IndexOf("A11站") > -1 || kw.IndexOf("A11坑口") > -1 || kw.IndexOf("捷運坑口站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("坑口") > -1))
                    {
                        kw = "捷運坑口站";
                    }
                    if (kw.IndexOf("A12站") > -1 || kw.IndexOf("A12機場第一航廈") > -1 || kw.IndexOf("A12第一航廈") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("第一航廈") > -1))
                    {
                        kw = "捷運機場第一航廈站";
                    }
                    if (kw.IndexOf("A13站") > -1 || kw.IndexOf("A13機場第二航廈") > -1 || kw.IndexOf("A13第二航廈") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("第二航廈") > -1))
                    {
                        kw = "捷運機場第二航廈站";
                    }
                    if (kw.IndexOf("A14站") > -1 || kw.IndexOf("A14A機場旅館") > -1 || kw.IndexOf("捷運機場旅館站") > -1 || kw.IndexOf("機場旅館站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("機場旅館") > -1))
                    {
                        kw = "捷運機場旅館站";
                    }
                    if (kw.IndexOf("A15站") > -1 || kw.IndexOf("A15大園") > -1 || kw.IndexOf("捷運大園站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("大園") > -1))
                    {
                        kw = "捷運大園站";
                    }
                    if (kw.IndexOf("A16站") > -1 || kw.IndexOf("A16橫山") > -1 || kw.IndexOf("捷運橫山站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("橫山") > -1))
                    {
                        kw = "捷運橫山站";
                    }
                    if (kw.IndexOf("A17站") > -1 || kw.IndexOf("A17領航") > -1 || kw.IndexOf("捷運領航站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("領航") > -1))
                    {
                        kw = "捷運領航站";
                    }
                    if (kw.IndexOf("A18站") > -1 || kw.IndexOf("A18高鐵桃園") > -1 || kw.IndexOf("捷運高鐵桃園站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("高鐵桃園") > -1))
                    {
                        kw = "捷運高鐵桃園站";
                    }
                    if (kw.IndexOf("A19站") > -1 || kw.IndexOf("A19桃園體育園區") > -1 || kw.IndexOf("捷運桃園體育園區站") > -1 || kw.IndexOf("A19體育園區") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("體育園區") > -1))
                    {
                        kw = "捷運桃園體育園區站";
                    }
                    if (kw.IndexOf("A20站") > -1 || kw.IndexOf("A20興南") > -1 || kw.IndexOf("捷運興南站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("興南") > -1))
                    {
                        kw = "捷運興南站";
                    }
                    if (kw.IndexOf("A21站") > -1 || kw.IndexOf("A21環北") > -1 || kw.IndexOf("21環北") > -1 || kw.IndexOf("捷運環北站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("環北") > -1))
                    {
                        kw = "捷運環北站";
                    }
                    if (kw.IndexOf("A22站") > -1 || kw.IndexOf("A22老街溪") > -1 || kw.IndexOf("捷運老街溪站") > -1 || ((kw.IndexOf("機捷") > -1 || kw.IndexOf("機場捷運") > -1) && kw.IndexOf("老街溪") > -1))
                    {
                        kw = "捷運老街溪站";
                    }
                    #endregion
                    #region 台中捷運
                    if (kw.IndexOf("監理站") == -1)
                    {
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("北屯總") > -1 || kw.IndexOf("敦富東街") > -1))
                        {
                            kw = "捷運北屯總站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("舊社") > -1 || kw.IndexOf("松竹路") > -1))
                        {
                            kw = "捷運舊社站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("松竹") > -1 || kw.IndexOf("北屯路") > -1))
                        {
                            kw = "捷運松竹站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("南屯") > -1 || kw.IndexOf("五權西路") > -1))
                        {
                            kw = "捷運南屯站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("豐樂公園") > -1 || kw.IndexOf("文心南路") > -1))
                        {
                            kw = "捷運豐樂公園站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("大慶") > -1 || kw.IndexOf("建國北路") > -1) && kw.IndexOf("火車") == -1)
                        {
                            kw = "捷運大慶站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("高鐵臺中") > -1 || kw.IndexOf("高鐵台中") > -1 || kw.IndexOf("高鐵東一路") > -1))
                        {
                            kw = "捷運高鐵臺中站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("九張犁") > -1)
                        {
                            kw = "捷運九張犁站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("九德") > -1)
                        {
                            kw = "捷運九德站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("烏日") > -1 && kw.IndexOf("火車") == -1)
                        {
                            kw = "捷運烏日站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("四維國小") > -1)
                        {
                            kw = "捷運四維國小站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("文心崇德") > -1)
                        {
                            kw = "捷運文心崇德站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("文心中清") > -1 || kw.IndexOf("中清文心") > -1))
                        {
                            kw = "捷運文心中清站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("文華高中") > -1)
                        {
                            kw = "捷運文華高中站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("文心櫻花") > -1)
                        {
                            kw = "捷運文心櫻花站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("台中") > -1 && kw.IndexOf("市政府") > -1)
                        {
                            kw = "台中市西屯區捷運市政府站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && kw.IndexOf("水安宮") > -1)
                        {
                            kw = "捷運水安宮站";
                        }
                        if ((kw.IndexOf("捷運") > -1 || kw.IndexOf("站") > -1) && (kw.IndexOf("文心森林公園") > -1 || (kw.IndexOf("森林公園") > -1 && kw.IndexOf("台中") > -1)))
                        {
                            kw = "捷運文心森林公園站";
                        }
                    }
                    #endregion
                    #region 高雄捷運
                    if (kw.IndexOf("R3小港") > -1 || kw.IndexOf("小港站") > -1 || (kw.IndexOf("小港") > -1 && kw.IndexOf("捷運") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R3", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運小港站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運小港站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R4高雄國際機場") > -1 || kw.IndexOf("R4高雄機場") > -1 || kw.IndexOf("高雄國際機場站") > -1 || kw.IndexOf("高雄機場站") > -1 || kw.IndexOf("高雄機場捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R4", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運高雄國際機場站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運高雄國際機場站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R4A草衙") > -1 || kw.IndexOf("R4草衙") > -1 || kw.IndexOf("草衙站") > -1 || kw.IndexOf("草衙捷運站") > -1 || kw.IndexOf("草衙捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R4", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運草衙站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運草衙站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R5前鎮高中") > -1 || kw.IndexOf("前鎮高中站") > -1 || kw.IndexOf("前鎮高中捷運") > -1 || (kw.IndexOf("R5") > -1 && kw.IndexOf("五甲") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R5", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運前鎮高中站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運前鎮高中站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R6凱旋") > -1 || kw.IndexOf("凱旋站") > -1 || kw.IndexOf("凱旋捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R6", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運凱旋站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運凱旋站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R7獅甲") > -1 || kw.IndexOf("獅甲站") > -1 || kw.IndexOf("獅甲捷運") > -1 || (kw.IndexOf("R7") > -1 && kw.IndexOf("勞工公園") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R7", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運獅甲站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運獅甲站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R8三多商圈") > -1 || kw.IndexOf("三多商圈站") > -1 || kw.IndexOf("三多商圈捷運") > -1 || (kw.IndexOf("R8") > -1 && kw.IndexOf("三多") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R8", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運三多商圈站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運三多商圈站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R9中央公園") > -1 || kw.IndexOf("中央公園站") > -1 || kw.IndexOf("中央公園捷運") > -1 || (kw.IndexOf("R9") > -1 && kw.IndexOf("中央公園") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R9", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運中央公園站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運中央公園站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R10美麗島") > -1 || kw.IndexOf("O5美麗島") > -1 || kw.IndexOf("美麗島站") > -1 || kw.IndexOf("美麗島捷運") > -1 || ((kw.IndexOf("R10") > -1 || kw.IndexOf("美麗島") > -1) || (kw.IndexOf("O5") > -1 || kw.IndexOf("美麗島") > -1)))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R10", "").Replace("O5", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運美麗島站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運美麗島站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R11高雄車站") > -1 || (kw.IndexOf("R11") > -1 && (kw.IndexOf("高雄") > -1 || kw.IndexOf("車站") > -1)) || (kw.IndexOf("捷運") > -1 && kw.IndexOf("高雄車站") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R11", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運高雄車站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運高雄車站2號出入口";
                        }
                    }
                    if (kw.IndexOf("R12後驛") > -1 || (kw.IndexOf("R12") > -1 && kw.IndexOf("高醫大") > -1) || kw.IndexOf("後驛站") > -1 || kw.IndexOf("後驛捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R12", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運後驛站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運後驛站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R13凹子底") > -1 || (kw.IndexOf("R13") > -1 && kw.IndexOf("凹子底") > -1) || kw.IndexOf("凹子底站") > -1 || kw.IndexOf("凹子底捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R13", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運凹子底站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運凹子底站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R14巨蛋") > -1 || kw.IndexOf("高雄巨蛋捷運") > -1 || (kw.IndexOf("R14") > -1 && kw.IndexOf("三民家商") > -1) || (kw.IndexOf("高雄") > -1 && kw.IndexOf("巨蛋") > -1 && kw.IndexOf("站") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R14", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運巨蛋站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運巨蛋站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R15生態園區") > -1 || (kw.IndexOf("R15") > -1 && kw.IndexOf("生態園區") > -1) || kw.IndexOf("生態園區站") > -1 || kw.IndexOf("生態園區捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R15", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運生態園區站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運生態園區站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R16左營") > -1 || (kw.IndexOf("R16") > -1 && kw.IndexOf("左營") > -1) || (kw.IndexOf("左營") > -1 && kw.IndexOf("捷運") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R16", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運左營站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運左營站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R17世運") > -1 || (kw.IndexOf("R17") > -1 && (kw.IndexOf("國家體育園區") > -1 || kw.IndexOf("國體") > -1)) || kw.IndexOf("世運站") > -1 || kw.IndexOf("世運捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R17", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運世運站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運世運站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R18油廠國小") > -1 || (kw.IndexOf("R18") > -1 && (kw.IndexOf("中山大學") > -1 || kw.IndexOf("中山") > -1)) || kw.IndexOf("油廠國小站") > -1 || kw.IndexOf("油廠國小捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R18", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運油廠國小站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運油廠國小站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R19楠梓加工區") > -1 || (kw.IndexOf("R19") > -1 && kw.IndexOf("楠梓") > -1) || kw.IndexOf("楠梓加工區站") > -1 || kw.IndexOf("楠梓科技產業園區站") > -1 || (kw.IndexOf("園區站") > -1 && kw.IndexOf("楠梓") > -1)
                           || (kw.IndexOf("捷運") > -1 && kw.IndexOf("楠梓加工") > -1) || (kw.IndexOf("楠梓捷運") > -1 && kw.IndexOf("號") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R19", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運楠梓加工區站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運楠梓加工區站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R20後勁") > -1 || (kw.IndexOf("R20") > -1 && kw.IndexOf("後勁") > -1) || (kw.IndexOf("捷運") > -1 && kw.IndexOf("後勁") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R20", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運後勁站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運後勁站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R21都會公園") > -1 || (kw.IndexOf("R21") > -1 && kw.IndexOf("都會公園") > -1) || (kw.IndexOf("捷運") > -1 && kw.IndexOf("都會公園") > -1) || kw.IndexOf("都會公園站") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R21", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運都會公園站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運都會公園站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R22青埔") > -1 || (kw.IndexOf("R22") > -1 && (kw.IndexOf("青埔") > -1 || kw.IndexOf("高科大") > -1)) || (kw.IndexOf("青埔") > -1 && kw.IndexOf("捷運") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R22", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運青埔站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運青埔站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R22A橋頭糖廠") > -1 || kw.IndexOf("R22橋頭糖廠") > -1 || (kw.IndexOf("R22") > -1 && kw.IndexOf("橋頭") > -1) || (kw.IndexOf("橋頭糖廠") > -1 && kw.IndexOf("站") > -1) || kw.IndexOf("橋頭糖廠捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R22", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運橋頭糖廠站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運橋頭糖廠站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R23橋頭火車站") > -1 || (kw.IndexOf("R23") > -1 && (kw.IndexOf("橋頭") > -1 || kw.IndexOf("火車站") > -1)) || (kw.IndexOf("橋頭") > -1 && kw.IndexOf("糖廠") == -1 && kw.IndexOf("捷運") > -1))
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R23", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運橋頭火車站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運橋頭火車站1號出入口";
                        }
                    }
                    if (kw.IndexOf("R24南岡山") > -1 || (kw.IndexOf("R24") > -1 && (kw.IndexOf("南岡山") > -1 || kw.IndexOf("火車站") > -1)) ||
                        (kw.IndexOf("南岡山") > -1 && kw.IndexOf("站") > -1) || (kw.IndexOf("岡山") > -1 && kw.IndexOf("高醫") > -1 && kw.IndexOf("站") > -1) ||
                        kw.IndexOf("南岡山捷運") > -1 || kw.IndexOf("高醫捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("R24", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運南岡山站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運南岡山站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O1西子灣") > -1 || (kw.IndexOf("O1") > -1 && (kw.IndexOf("西子灣") > -1 || kw.IndexOf("中山大學") > -1)) || (kw.IndexOf("捷運") > -1 && kw.IndexOf("哈瑪星") > -1) || kw.IndexOf("西子灣捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O1", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運西子灣站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運西子灣站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O2鹽埕埔") > -1 || (kw.IndexOf("O2") > -1 && kw.IndexOf("鹽埕埔") > -1) || (kw.IndexOf("站") > -1 && kw.IndexOf("鹽埕埔") > -1) || kw.IndexOf("鹽埕埔捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O2", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運鹽埕埔站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運鹽埕埔站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O4市議會") > -1 || (kw.IndexOf("O4") > -1 && kw.IndexOf("市議會") > -1) || (kw.IndexOf("O4") > -1 && kw.IndexOf("前金") > -1) ||
                        kw.IndexOf("前金站") > -1 || kw.IndexOf("市議會站") > -1 || kw.IndexOf("市議會捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O4", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運市議會站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運市議會站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O6信義國小") > -1 || (kw.IndexOf("O6") > -1 && kw.IndexOf("信義國小") > -1) || kw.IndexOf("信義國小站") > -1 || kw.IndexOf("信義國小捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O6", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運信義國小站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運信義國小站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O7文化中心") > -1 || (kw.IndexOf("O7") > -1 && kw.IndexOf("文化中心") > -1) || kw.IndexOf("文化中心站") > -1 || kw.IndexOf("文化中心捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O7", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運文化中心站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運文化中心站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O8五塊厝") > -1 || (kw.IndexOf("O8") > -1 && kw.IndexOf("五塊厝") > -1) || kw.IndexOf("五塊厝站") > -1 || kw.IndexOf("五塊厝捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O8", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運五塊厝站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運五塊厝站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O9技擊館") > -1 || (kw.IndexOf("O9") > -1 && kw.IndexOf("技擊館") > -1) || (kw.IndexOf("捷運") > -1 && kw.IndexOf("技擊館") > -1) || kw.IndexOf("技擊館站") > -1 || kw.IndexOf("技擊館捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O9", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運技擊館站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運技擊館站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O10衛武營") > -1 || (kw.IndexOf("O10") > -1 && kw.IndexOf("衛武營") > -1) || kw.IndexOf("衛武營站") > -1 || kw.IndexOf("衛武營捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O10", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運衛武營站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運衛武營站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O11鳳山西") > -1 || (kw.IndexOf("O11") > -1 && kw.IndexOf("鳳山西") > -1) || kw.IndexOf("鳳山西站") > -1 || kw.IndexOf("鳳山西捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O11", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運鳳山西站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運鳳山西站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O12鳳山") > -1 || (kw.IndexOf("O12") > -1 && kw.IndexOf("鳳山") > -1) || kw.IndexOf("鳳山站") > -1 || kw.IndexOf("鳳山捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O12", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運鳳山站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運鳳山站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O13大東") > -1 || (kw.IndexOf("O13") > -1 && kw.IndexOf("大東") > -1) || kw.IndexOf("大東站") > -1 || kw.IndexOf("大東捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O13", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運大東站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運大東站1號出入口";
                        }
                    }
                    if (kw.IndexOf("O14鳳山國中") > -1 || (kw.IndexOf("O14") > -1 && kw.IndexOf("鳳山國中") > -1) || kw.IndexOf("鳳山國中站") > -1 || kw.IndexOf("鳳山國中捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("O14", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運鳳山國中站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運鳳山國中站1號出入口";
                        }
                    }
                    if (kw.IndexOf("OT1大寮") > -1 || (kw.IndexOf("OT1") > -1 && (kw.IndexOf("大寮") > -1 || kw.IndexOf("前庄") > -1)) || kw.IndexOf("大寮站") > -1 || kw.IndexOf("大寮捷運") > -1)
                    {
                        var getNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("OT1", ""), IsCrossRoads = false, doChineseNum = true });
                        if (!string.IsNullOrEmpty(getNum.Num))
                        {
                            kw = "捷運大寮站" + getNum.Num;
                        }
                        else
                        {
                            kw = "捷運大寮站1號出入口";
                        }
                    }
                    #endregion
                    #region 高雄輕軌
                    if (kw.IndexOf("C1籬仔內") > -1 || kw.IndexOf("籬仔內站") > -1 || (kw.IndexOf("C1") > -1 && kw.IndexOf("籬仔內") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("籬仔內") > -1))
                    {
                        kw = "輕軌籬仔內站";
                    }
                    if (kw.IndexOf("C2凱旋瑞田") > -1 || kw.IndexOf("凱旋瑞田站") > -1 || (kw.IndexOf("C2") > -1 && kw.IndexOf("凱旋瑞田") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("凱旋瑞田") > -1))
                    {
                        kw = "輕軌凱旋瑞田站";
                    }
                    if (kw.IndexOf("C3前鎮之星") > -1 || kw.IndexOf("前鎮之星站") > -1 || (kw.IndexOf("C3") > -1 && kw.IndexOf("前鎮之星") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("前鎮之星") > -1))
                    {
                        kw = "輕軌前鎮之星站";
                    }
                    if (kw.IndexOf("C4凱旋中華") > -1 || kw.IndexOf("凱旋中華站") > -1 || (kw.IndexOf("C4") > -1 && kw.IndexOf("凱旋中華") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("凱旋中華") > -1))
                    {
                        kw = "輕軌凱旋中華站";
                    }
                    if (kw.IndexOf("C5夢時代") > -1 || kw.IndexOf("夢時代站") > -1 || (kw.IndexOf("C5") > -1 && kw.IndexOf("夢時代") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("夢時代") > -1))
                    {
                        kw = "輕軌夢時代站";
                    }
                    if (kw.IndexOf("C6經貿園區") > -1 || kw.IndexOf("經貿園區站") > -1 || (kw.IndexOf("C6") > -1 && kw.IndexOf("經貿園區") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("經貿園區") > -1))
                    {
                        kw = "輕軌經貿園區站";
                    }
                    if (kw.IndexOf("C7軟體園區") > -1 || (kw.IndexOf("軟體園區站") > -1 && kw.IndexOf("南港軟體園區站") == -1) || (kw.IndexOf("C7") > -1 && kw.IndexOf("軟體園區") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("軟體園區") > -1))
                    {
                        kw = "輕軌軟體園區站";
                    }
                    if (kw.IndexOf("C8高雄展覽館") > -1 || kw.IndexOf("高雄展覽館站") > -1 || (kw.IndexOf("C8") > -1 && kw.IndexOf("高雄展覽館") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("高雄展覽館") > -1))
                    {
                        kw = "輕軌高雄展覽館站";
                    }
                    if (kw.IndexOf("C10光榮碼頭") > -1 || kw.IndexOf("光榮碼頭站") > -1 || (kw.IndexOf("C10") > -1 && kw.IndexOf("光榮碼頭") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("光榮碼頭") > -1))
                    {
                        kw = "輕軌光榮碼頭站";
                    }
                    if (kw.IndexOf("C11真愛碼頭") > -1 || kw.IndexOf("真愛碼頭站") > -1 || (kw.IndexOf("C11") > -1 && kw.IndexOf("真愛碼頭") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("真愛碼頭") > -1))
                    {
                        kw = "輕軌真愛碼頭站";
                    }
                    if (kw.IndexOf("C12駁二大義") > -1 || kw.IndexOf("駁二大義站") > -1 || (kw.IndexOf("C12") > -1 && kw.IndexOf("駁二大義") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("駁二大義") > -1))
                    {
                        kw = "輕軌駁二大義站";
                    }
                    if (kw.IndexOf("C13駁二蓬萊") > -1 || kw.IndexOf("駁二蓬萊站") > -1 || (kw.IndexOf("C13") > -1 && kw.IndexOf("駁二蓬萊") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("駁二蓬萊") > -1))
                    {
                        kw = "輕軌駁二蓬萊站";
                    }
                    if (kw.IndexOf("C14哈瑪星") > -1 || (kw.IndexOf("C14") > -1 && kw.IndexOf("哈瑪星") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("哈瑪星") > -1))
                    {
                        kw = "輕軌哈瑪星站";
                    }
                    if (kw.IndexOf("C15壽山公園") > -1 || kw.IndexOf("壽山公園站") > -1 || (kw.IndexOf("C15") > -1 && kw.IndexOf("壽山公園") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("壽山公園") > -1))
                    {
                        kw = "輕軌壽山公園站";
                    }
                    if (kw.IndexOf("C16文武聖殿") > -1 || kw.IndexOf("文武聖殿站") > -1 || (kw.IndexOf("C16") > -1 && kw.IndexOf("文武聖殿") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("文武聖殿") > -1))
                    {
                        kw = "輕軌文武聖殿站";
                    }
                    if (kw.IndexOf("C17鼓山區公所") > -1 || kw.IndexOf("鼓山區公所站") > -1 || (kw.IndexOf("C17") > -1 && kw.IndexOf("鼓山區公所") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("鼓山區公所") > -1))
                    {
                        kw = "輕軌鼓山區公所站";
                    }
                    if (kw.IndexOf("C18鼓山") > -1 || kw.IndexOf("鼓山站") > -1 || (kw.IndexOf("C18") > -1 && kw.IndexOf("鼓山") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("鼓山") > -1 && kw.IndexOf("鼓山區公所") == -1))
                    {
                        kw = "輕軌鼓山站";
                    }
                    if (kw.IndexOf("C19馬卡道") > -1 || kw.IndexOf("馬卡道站") > -1 || (kw.IndexOf("C19") > -1 && kw.IndexOf("馬卡道") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("馬卡道") > -1))
                    {
                        kw = "輕軌馬卡道站";
                    }
                    if (kw.IndexOf("C20臺鐵美術館") > -1 || kw.IndexOf("臺鐵美術館站") > -1 || (kw.IndexOf("C20") > -1 && kw.IndexOf("臺鐵美術館") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("臺鐵美術館") > -1))
                    {
                        kw = "輕軌臺鐵美術館站";
                    }
                    if (kw.IndexOf("C21A內惟藝術中心") > -1 || kw.IndexOf("C21內惟藝術中心") > -1 || kw.IndexOf("內惟藝術中心站") > -1 || (kw.IndexOf("C21") > -1 && kw.IndexOf("內惟藝術中心") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("內惟藝術中心") > -1))
                    {
                        kw = "輕軌內惟藝術中心站";
                    }
                    if (kw.IndexOf("C21美術館") > -1 || (kw.IndexOf("美術館站") > -1 && kw.IndexOf("臺鐵") == -1) || (kw.IndexOf("C21") > -1 && kw.IndexOf("美術館") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("美術館") > -1 && kw.IndexOf("臺鐵美術館") == -1))
                    {
                        kw = "輕軌美術館站";
                    }
                    if (kw.IndexOf("C22聯合醫院") > -1 || kw.IndexOf("聯合醫院站") > -1 || (kw.IndexOf("C22") > -1 && kw.IndexOf("聯合醫院") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("聯合醫院") > -1))
                    {
                        kw = "輕軌聯合醫院站";
                    }
                    if (kw.IndexOf("C23龍華國小") > -1 || kw.IndexOf("龍華國小站") > -1 || (kw.IndexOf("C23") > -1 && kw.IndexOf("龍華國小") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("龍華國小") > -1))
                    {
                        kw = "輕軌龍華國小站";
                    }
                    if (kw.IndexOf("C24愛河之心") > -1 || kw.IndexOf("愛河之心站") > -1 || (kw.IndexOf("C24") > -1 && kw.IndexOf("愛河之心") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("愛河之心") > -1))
                    {
                        kw = "輕軌愛河之心站";
                    }
                    if (kw.IndexOf("C25新上國小") > -1 || kw.IndexOf("新上國小站") > -1 || (kw.IndexOf("C25") > -1 && kw.IndexOf("新上國小") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("新上國小") > -1))
                    {
                        kw = "輕軌新上國小站";
                    }
                    if (kw.IndexOf("C26灣仔內") > -1 || kw.IndexOf("C26民族大順") > -1 || kw.IndexOf("灣仔內站") > -1 || kw.IndexOf("民族大順站") > -1 ||
                        (kw.IndexOf("C26") > -1 && (kw.IndexOf("灣仔內") > -1 || kw.IndexOf("民族大順") > -1)) || (kw.IndexOf("輕軌") > -1 && (kw.IndexOf("灣仔內") > -1 || kw.IndexOf("民族大順") > -1)))
                    {
                        kw = "輕軌灣仔內站";
                    }
                    if (kw.IndexOf("C27鼎山街") > -1 || kw.IndexOf("C27灣仔內") > -1 || kw.IndexOf("鼎山街站") > -1 ||
                     (kw.IndexOf("C27") > -1 && (kw.IndexOf("鼎山街") > -1 || kw.IndexOf("灣仔內") > -1)) || (kw.IndexOf("輕軌") > -1 && (kw.IndexOf("鼎山街") > -1)))
                    {
                        kw = "輕軌鼎山街站";
                    }
                    if (kw.IndexOf("C28高雄高工") > -1 || kw.IndexOf("高雄高工站") > -1 || (kw.IndexOf("C28") > -1 && kw.IndexOf("高雄高工") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("高雄高工") > -1))
                    {
                        kw = "輕軌高雄高工站";
                    }
                    if (kw.IndexOf("C28高雄高工") > -1 || kw.IndexOf("高雄高工站") > -1 || (kw.IndexOf("C28") > -1 && kw.IndexOf("高雄高工") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("高雄高工") > -1))
                    {
                        kw = "輕軌高雄高工站";
                    }
                    if (kw.IndexOf("C29樹德家商") > -1 || kw.IndexOf("樹德家商站") > -1 || (kw.IndexOf("C29") > -1 && kw.IndexOf("樹德家商") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("樹德家商") > -1))
                    {
                        kw = "輕軌樹德家商站";
                    }
                    if (kw.IndexOf("C30科工館") > -1 || kw.IndexOf("科工館站") > -1 || (kw.IndexOf("C30") > -1 && kw.IndexOf("科工館") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("科工館") > -1))
                    {
                        kw = "輕軌科工館站";
                    }
                    if (kw.IndexOf("C31聖功醫院") > -1 || kw.IndexOf("聖功醫院站") > -1 || (kw.IndexOf("C31") > -1 && kw.IndexOf("聖功醫院") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("聖功醫院") > -1))
                    {
                        kw = "輕軌聖功醫院站";
                    }
                    if (kw.IndexOf("C32凱旋公園") > -1 || kw.IndexOf("凱旋公園站") > -1 || (kw.IndexOf("C32") > -1 && kw.IndexOf("凱旋公園") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("凱旋公園") > -1))
                    {
                        kw = "輕軌凱旋公園站";
                    }
                    if (kw.IndexOf("C33衛生局") > -1 || kw.IndexOf("衛生局站") > -1 || (kw.IndexOf("C33") > -1 && kw.IndexOf("衛生局") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("衛生局") > -1))
                    {
                        kw = "輕軌衛生局站";
                    }
                    if (kw.IndexOf("C34五權國小") > -1 || kw.IndexOf("五權國小站") > -1 || (kw.IndexOf("C34") > -1 && kw.IndexOf("五權國小") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("五權國小") > -1))
                    {
                        kw = "輕軌五權國小站";
                    }
                    if (kw.IndexOf("C35凱旋武昌") > -1 || kw.IndexOf("凱旋武昌站") > -1 || (kw.IndexOf("C35") > -1 && kw.IndexOf("五權國小") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("凱旋武昌") > -1))
                    {
                        kw = "輕軌凱旋武昌站";
                    }
                    if (kw.IndexOf("36凱旋二聖") > -1 || kw.IndexOf("凱旋二聖站") > -1 || (kw.IndexOf("C36") > -1 && kw.IndexOf("凱旋二聖") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("凱旋二聖") > -1))
                    {
                        kw = "輕軌凱旋二聖站";
                    }
                    if (kw.IndexOf("C37輕軌機廠") > -1 || kw.IndexOf("輕軌機廠站") > -1 || (kw.IndexOf("C37") > -1 && kw.IndexOf("輕軌機廠") > -1) || (kw.IndexOf("輕軌") > -1 && (kw.IndexOf("機廠") > -1 || kw.IndexOf("機場") > -1)))
                    {
                        kw = "輕軌機廠站";
                    }
                    #endregion
                    #region 安坑輕軌
                    if (kw.IndexOf("K01雙城") > -1 || kw.IndexOf("雙城站") > -1 || (kw.IndexOf("K01") > -1 && kw.IndexOf("雙城") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("雙城") > -1))
                    {
                        kw = "輕軌雙城站";
                    }
                    if (kw.IndexOf("K02雙城") > -1 || kw.IndexOf("玫瑰中國城站") > -1 || (kw.IndexOf("K02") > -1 && kw.IndexOf("玫瑰中國城") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("玫瑰中國城") > -1))
                    {
                        kw = "輕軌玫瑰中國城站出入口";
                    }
                    if (kw.IndexOf("K03台北小城") > -1 || kw.IndexOf("台北小城站") > -1 || (kw.IndexOf("K03") > -1 && kw.IndexOf("台北小城") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("台北小城") > -1))
                    {
                        kw = "輕軌台北小城站";
                    }
                    if (kw.IndexOf("K04耕莘安康") > -1 || kw.IndexOf("耕莘安康院區站") > -1 || (kw.IndexOf("K04") > -1 && kw.IndexOf("耕莘安康") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("耕莘安康") > -1))
                    {
                        kw = "輕軌耕莘安康院區站";
                        if (kw.IndexOf("十四張") > -1)
                        {
                            kw = "輕軌耕莘安康院區站往十四張";
                        }
                        if (kw.IndexOf("雙城") > -1)
                        {
                            kw = "輕軌耕莘安康院區站往雙城";
                        }
                    }
                    if (kw.IndexOf("K05景文科大") > -1 || kw.IndexOf("景文科大站") > -1 || (kw.IndexOf("K05") > -1 && kw.IndexOf("景文科大") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("景文科大") > -1))
                    {
                        kw = "輕軌景文科大站";
                    }
                    if (kw.IndexOf("K06安康") > -1 || (kw.IndexOf("安康站") > -1 && kw.IndexOf("新店") > -1) || (kw.IndexOf("K06") > -1 && kw.IndexOf("安康") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("安康") > -1))
                    {
                        kw = "輕軌安康站出入口";
                    }
                    if (kw.IndexOf("K07陽光運動公園") > -1 || kw.IndexOf("陽光運動公園站") > -1 || (kw.IndexOf("K07") > -1 && kw.IndexOf("陽光運動公園") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("陽光運動公園") > -1))
                    {
                        kw = "輕軌陽光運動公園站出入口";
                    }
                    if (kw.IndexOf("K08景文科大") > -1 || kw.IndexOf("新和國小站") > -1 || (kw.IndexOf("K08") > -1 && kw.IndexOf("新和國小") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("新和國小") > -1))
                    {
                        kw = "輕軌新和國小站出入口";
                    }
                    if (kw.IndexOf("K09十四張") > -1 || (kw.IndexOf("K09") > -1 && kw.IndexOf("十四張") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("十四張") > -1))
                    {
                        kw = "輕軌十四張站出入口";
                    }
                    #endregion
                    #region 淡水輕軌
                    if (kw.IndexOf("V01雙城") > -1 || (kw.IndexOf("V01") > -1 && kw.IndexOf("紅樹林") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("紅樹林") > -1))
                    {
                        kw = "輕軌紅樹林站";
                    }
                    if (kw.IndexOf("V02竿蓁林") > -1 || kw.IndexOf("竿蓁林站") > -1 || (kw.IndexOf("V02") > -1 && kw.IndexOf("竿蓁林") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("竿蓁林") > -1))
                    {
                        kw = "輕軌竿蓁林站";
                    }
                    if (kw.IndexOf("V03竿蓁林") > -1 || kw.IndexOf("淡金鄧公站") > -1 || (kw.IndexOf("V03") > -1 && kw.IndexOf("淡金鄧公") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("淡金鄧公") > -1))
                    {
                        kw = "輕軌淡金鄧公站";
                    }
                    if (kw.IndexOf("V04竿蓁林") > -1 || kw.IndexOf("淡江大學站") > -1 || (kw.IndexOf("V04") > -1 && kw.IndexOf("淡江大學") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("淡江大學") > -1))
                    {
                        kw = "輕軌淡江大學站";
                    }
                    if (kw.IndexOf("V05淡金北新") > -1 || kw.IndexOf("淡金北新站") > -1 || (kw.IndexOf("V05") > -1 && kw.IndexOf("淡金北新") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("淡金北新") > -1))
                    {
                        kw = "輕軌淡金北新站";
                    }
                    if (kw.IndexOf("V06新市一路") > -1 || kw.IndexOf("新市一路站") > -1 || (kw.IndexOf("V06") > -1 && kw.IndexOf("新市一路") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("新市一路") > -1))
                    {
                        kw = "輕軌新市一路站";
                    }
                    if (kw.IndexOf("V07淡水行政中心") > -1 || kw.IndexOf("淡水行政中心站") > -1 || (kw.IndexOf("V07") > -1 && kw.IndexOf("淡水行政中心") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("淡水行政中心") > -1))
                    {
                        kw = "輕軌淡水行政中心站";
                    }
                    if (kw.IndexOf("V08濱海義山") > -1 || kw.IndexOf("濱海義山站") > -1 || (kw.IndexOf("V08") > -1 && kw.IndexOf("濱海義山") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("濱海義山") > -1))
                    {
                        kw = "輕軌濱海義山站";
                    }
                    if (kw.IndexOf("V09濱海沙崙") > -1 || kw.IndexOf("濱海沙崙站") > -1 || (kw.IndexOf("V09") > -1 && kw.IndexOf("濱海沙崙") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("濱海沙崙") > -1))
                    {
                        kw = "輕軌濱海沙崙站";
                    }
                    if (kw.IndexOf("V10淡海新市鎮") > -1 || kw.IndexOf("淡海新市鎮站") > -1 || (kw.IndexOf("V10") > -1 && kw.IndexOf("淡海新市鎮") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("淡海新市鎮") > -1))
                    {
                        kw = "輕軌淡海新市鎮站";
                    }
                    if (kw.IndexOf("V11崁頂") > -1 || kw.IndexOf("崁頂站") > -1 || (kw.IndexOf("V11") > -1 && kw.IndexOf("崁頂") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("崁頂") > -1))
                    {
                        kw = "輕軌崁頂站";
                    }
                    if (kw.IndexOf("V26淡水漁人碼頭") > -1 || kw.IndexOf("淡水漁人碼頭站") > -1 || (kw.IndexOf("V26") > -1 && kw.IndexOf("漁人碼頭") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("漁人碼頭") > -1))
                    {
                        kw = "輕軌淡水漁人碼頭站";
                    }
                    if (kw.IndexOf("V27沙崙") > -1 || (kw.IndexOf("沙崙站") > -1 && kw.IndexOf("濱海") == -1) || (kw.IndexOf("V27") > -1 && kw.IndexOf("沙崙") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("沙崙") > -1 && kw.IndexOf("濱海") == -1))
                    {
                        kw = "輕軌沙崙站";
                    }
                    if (kw.IndexOf("V28淡水漁人碼頭") > -1 || kw.IndexOf("海洋大學站") > -1 || (kw.IndexOf("V28") > -1 && kw.IndexOf("海洋大學") > -1) || (kw.IndexOf("輕軌") > -1 && kw.IndexOf("海洋大學") > -1))
                    {
                        kw = "輕軌台北海洋大學站";
                    }
                    #endregion
                    #region 機場
                    if (kw.IndexOf("機場") > -1 && kw.IndexOf("捷運") == -1 && checkNum == -1)
                    {
                        if (kw.IndexOf("桃園") > -1)
                        {
                            var isC = false;
                            if (kw.IndexOf("二航") > -1) { kw = "桃園國際機場第二航廈"; isC = true; }
                            if (!isC) { kw = "臺灣桃園國際機場第一航廈"; }
                        }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("清泉崗") > -1) { kw = "台中國際機場"; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("小港") > -1) { kw = "高雄國際機場國際航廈出境出入口-"; }
                    }
                    if (kw.IndexOf("桃園") > -1 && kw.IndexOf("航廈") > -1 && kw.IndexOf("機場") == -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("二") > -1) { kw = "桃園國際機場第二航廈"; isC = true; }
                        if (!isC) { kw = "臺灣桃園國際機場第一航廈"; }
                    }
                    #endregion
                    #region 其他交通
                    if (kw == "北車" || kw == "台北車站" || kw == "台北火車站" ||
                       (kw.IndexOf("台北火車站") > -1 && kw.IndexOf("門") > -1) ||
                       (kw.IndexOf("北車") > -1 && kw.IndexOf("門") > -1) ||
                       (kw.IndexOf("台北車站") > -1 && kw.IndexOf("門") > -1 && checkNum == -1))
                    {
                        kw = "台北火車站-請至捷運M3出口搭車"; isReplaceMarkName = true;
                    }
                    if (kw == "板橋車站" || kw == "板橋火車站" ||
                     ((kw.IndexOf("板橋車站") > -1 || kw.IndexOf("板橋火車站") > -1) && kw.IndexOf("門") > -1) ||
                     ((kw.IndexOf("板橋車站") > -1 || kw.IndexOf("板橋火車站") > -1) && kw.IndexOf("計程車") > -1))
                    {
                        kw = "板橋車站-請至北二門搭車"; isReplaceMarkName = true;
                    }
                    if (kw == "台北市政府")
                    {
                        kw = "台北市政府-市府路出入口"; isReplaceMarkName = true;
                    }
                    else if (kw.IndexOf("台北市政府") > -1 && kw.IndexOf("路") > -1 && checkNum == -1)
                    {
                        var idx = kw.IndexOf("路");
                        if (idx > 1)
                        {
                            var tempRoad = kw.Substring(idx - 2, 2);
                            kw = "台北市政府-" + tempRoad + "路出入口";
                            isReplaceMarkName = true;
                        }
                    }
                    if (kw == "國父紀念館")
                    {
                        kw = "國父紀念館-忠孝東路出入口"; isReplaceMarkName = true;
                    }
                    else if (kw.IndexOf("國父紀念館") > -1 && kw.IndexOf("路") > -1 && checkNum == -1 && kw.IndexOf("捷運") == -1)
                    {
                        var tempRoad = "";
                        if (kw.IndexOf("逸仙路") > -1 || kw.IndexOf("仁愛路") > -1)
                        {
                            var idx = kw.IndexOf("路");
                            if (idx > 1)
                            {
                                tempRoad = kw.Substring(idx - 2, 2);
                            }
                        }
                        if (kw.IndexOf("光復南路") > -1 || kw.IndexOf("忠孝東路") > -1)
                        {
                            var idx = kw.IndexOf("路");
                            if (idx > 2)
                            {
                                tempRoad = kw.Substring(idx - 3, 3);
                            }
                        }
                        if (!string.IsNullOrEmpty(tempRoad))
                        {
                            kw = "國父紀念館-" + tempRoad + "路出入口";
                            isReplaceMarkName = true;
                        }
                    }
                    if (kw == "台北101")
                    {
                        kw = "台北101-信義路出入口"; isReplaceMarkName = true;
                    }
                    else if (kw.IndexOf("台北101") > -1 && kw.IndexOf("路") > -1 && checkNum == -1 && kw.IndexOf("捷運") == -1)
                    {
                        var idx = kw.IndexOf("路");
                        if (idx > 1)
                        {
                            var tempRoad = kw.Substring(idx - 2, 2);
                            kw = "台北101-" + tempRoad + "路出入口";
                            isReplaceMarkName = true;
                        }
                    }
                    if (kw == "中正紀念堂" || kw == "自由廣場")
                    {
                        kw = "中正紀念堂-自由廣場";
                        isReplaceMarkName = true;
                    }
                    else if (kw.IndexOf("中正紀念堂") > -1 && checkNum == -1 && kw.IndexOf("捷運") == -1)
                    {
                        if (kw.IndexOf("大忠門") > -1)
                        {
                            kw = "中正紀念堂-大忠門";
                        }
                        if (kw.IndexOf("大孝門") > -1)
                        {
                            kw = "中正紀念堂大孝門";
                        }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("京站") > -1 && kw.IndexOf("轉運站") > -1 && checkNum == -1)
                    {
                        kw = "台北轉運站";
                    }
                    if (kw.IndexOf("南港車站") > -1 && kw.IndexOf("出口") > -1 && kw.IndexOf("捷運") == -1 && checkNum == -1)
                    {
                        kw = "捷運南港站";
                    }
                    if (kw.IndexOf("松山") > -1 && kw.IndexOf("車站") > -1 && checkNum == -1 && kw.IndexOf("捷運") == -1)
                    {
                        if (kw.IndexOf("西") > -1)
                        {
                            kw = "松山火車站-西門";
                        }
                        if (kw.IndexOf("東") > -1)
                        {
                            kw = "松山火車站-東門";
                        }
                        if (kw.IndexOf("南") > -1)
                        {
                            kw = "松山火車站-南門(靠市民大道)";
                        }
                        if (kw.IndexOf("北一") > -1)
                        {
                            kw = "松山火車站-北一門(靠市民大道)";
                        }
                        if (kw.IndexOf("北二") > -1)
                        {
                            kw = "松山火車站-北二門(靠市民大道)";
                        }
                    }
                    if (kw.IndexOf("彰化") > -1 && kw.IndexOf("社頭火車站") > -1 && kw.IndexOf("彰化縣") == -1)
                    {
                        kw = "彰化縣社頭火車站";
                    }
                    if (kw.IndexOf("台北") > -1 && kw.IndexOf("市府") > -1 && kw.IndexOf("捷運") > -1 && checkNum == -1 && kw.IndexOf("市政府站") == -1)
                    {
                        kw = "台北市捷運市政府站";
                    }
                    if (kw.IndexOf("台中") > -1 && kw.IndexOf("市府") > -1 && kw.IndexOf("捷運") > -1 && checkNum == -1 && kw.IndexOf("市政府站") == -1)
                    {
                        kw = "台中市捷運市政府站";
                    }
                    if (kw.IndexOf("七堵車站") > -1 && kw.IndexOf("舊") == -1) { kw = "七堵火車站"; }
                    if (kw.IndexOf("汐止火車站") > -1) { kw = "汐止火車站"; }
                    if (kw.IndexOf("長庚轉運站") > -1 || (kw.IndexOf("轉運站") > -1 && kw.IndexOf("桃園") > -1)) { kw = "桃園長庚轉運站"; }
                    #endregion
                    #endregion
                    #region 餐廳/飯店
                    if (kw.IndexOf("晶宴會館") > -1)
                    {
                        if (kw.IndexOf("民生") > -1) { kw = "晶宴會館-民生館"; }
                        if (kw.IndexOf("民權") > -1) { kw = "晶宴會館-民權館"; }
                        if (kw.IndexOf("中和") > -1) { kw = "晶宴會館-中和館"; }
                        if (kw.IndexOf("新莊") > -1 || kw.IndexOf("思源路") > -1) { kw = "晶宴會館-新莊館"; }
                        if (kw.IndexOf("府中") > -1 || kw.IndexOf("板橋") > -1) { kw = "晶宴會館-府中館"; }
                        if (kw.IndexOf("海洋") > -1 || kw.IndexOf("貢寮") > -1 || kw.IndexOf("和美街") > -1) { kw = "晶宴會館-海洋莊園"; }
                        if (kw.IndexOf("府中") > -1 || kw.IndexOf("板橋") > -1) { kw = "晶宴會館-府中館"; }
                        if ((kw.IndexOf("公道五路") > -1 || kw.IndexOf("新竹") > -1) && kw.IndexOf("三段") == -1) { kw = "晶宴會館-新竹館"; }
                        if (kw.IndexOf("御豐") > -1 || kw.IndexOf("公道五路三段") > -1 || (kw.IndexOf("新竹") > -1 && kw.IndexOf("三段") > -1)) { kw = "晶宴會館-御豐館"; }
                        if (kw.IndexOf("竹北") > -1 || kw.IndexOf("光明六路") > -1) { kw = "晶宴會館-竹北館"; }
                        if (kw.IndexOf("桃園") > -1 || kw.IndexOf("南平路") > -1) { kw = "晶宴會館-桃園館"; }
                        if (kw.IndexOf("湖口") > -1 || kw.IndexOf("新湖路") > -1) { kw = "湖口東北晶宴會館"; }
                    }
                    if (kw.IndexOf("錢都") > -1 && (kw.IndexOf("刷刷鍋") > -1 || kw.IndexOf("涮涮鍋") > -1)) { kw = kw.Replace("錢都", "錢都-"); }
                    if (kw.IndexOf("春水堂") > -1)
                    {
                        if (kw.IndexOf("市政店") > -1) { kw = "春水堂-台中市政店"; }
                    }
                    if (kw.IndexOf("屋馬") > -1)
                    {
                        if (kw.IndexOf("中港") > -1 || kw.IndexOf("台灣大道") > -1) { kw = "屋馬燒肉料亭-中港店"; }
                        if (kw.IndexOf("中友") > -1 || kw.IndexOf("才北路") > -1) { kw = "屋馬燒肉料亭-中友店"; }
                        if (kw.IndexOf("文心") > -1) { kw = "屋馬燒肉料亭-文心店"; }
                        if (kw.IndexOf("國安") > -1) { kw = "屋馬燒肉料亭-國安店"; }
                        if (kw.IndexOf("崇德") > -1) { kw = "屋馬燒肉料亭-崇德店"; }
                        if (kw.IndexOf("園邸") > -1 || kw.IndexOf("公益路") > -1) { kw = "麗加園邸酒店屋馬燒肉料亭-園邸店"; }
                    }
                    if (kw.IndexOf("船老大") > -1)
                    {
                        if (kw.IndexOf("嘉義") > -1) { kw = "船老大海鮮漁村"; }
                        if (kw.IndexOf("新竹") > -1) { kw = "船老大海鮮"; }
                        if (kw.IndexOf("民宿") > -1 || kw.IndexOf("連江") > -1) { kw = "連江縣莒光鄉大坪村72號"; }
                    }
                    if (kw.IndexOf("國賓大飯店") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("中山北路") > -1 || kw.IndexOf("台北國賓") > -1) { kw = "台北國賓大飯店"; isC = true; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("前金") > -1 || kw.IndexOf("民生二路") > -1) { kw = "高雄國賓大飯店"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("東區") > -1 || kw.IndexOf("中華路") > -1) { kw = "新竹國賓大飯店"; isC = true; }
                        if (!isC) { kw = "國賓大飯店"; }
                    }
                    if (kw.IndexOf("台南") > -1 && (kw.IndexOf("富林") > -1 || kw.IndexOf("富霖") > -1) && kw.IndexOf("富林路") == -1 && (kw.IndexOf("餐廳") > -1 || kw.IndexOf("餐飲") > -1 || kw.IndexOf("會館") > -1))
                    {
                        kw = "富霖餐飲-華平宴會館";
                    }
                    if (kw.IndexOf("福容大飯店") > -1 && kw.IndexOf("廳") == -1)
                    {
                        if (kw.IndexOf("漁人碼頭") > -1 || kw.IndexOf("淡水") > -1 || kw.IndexOf("觀海路") > -1) { kw = "福容大飯店-淡水漁人碼頭店"; }
                        if (kw.IndexOf("深坑") > -1 || kw.IndexOf("北深路") > -1 || kw.IndexOf("二館") > -1) { kw = "福容大飯店-台北二館"; }
                        if (kw.IndexOf("中壢") > -1) { kw = "福容大飯店-中壢店"; }
                        if (kw.IndexOf("A8店") > -1 || kw.IndexOf("機場捷運") > -1 || kw.IndexOf("復興一路") > -1 || kw.IndexOf("龜山") > -1) { kw = "福容大飯店-桃園機場捷運A8店"; }
                        if (kw.IndexOf("大興西路") > -1 || kw.IndexOf("桃園") > -1) { kw = "福容大飯店-桃園店"; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("鹽埕") > -1 || kw.IndexOf("五福四路") > -1) { kw = "福容大飯店-高雄店"; }
                        if (kw.IndexOf("麗寶樂園") > -1 || kw.IndexOf("台中") > -1 || kw.IndexOf("后里") > -1) { kw = "福容大飯店-麗寶樂園店"; }
                        if ((kw.IndexOf("台北") > -1 || kw.IndexOf("大安") > -1 || kw.IndexOf("建國南路") > -1 || kw.IndexOf("一館") > -1) && kw.IndexOf("-") == -1) { kw = "福容大飯店-台北一館"; }
                        if (kw.IndexOf("花蓮") > -1) { kw = "福容大飯店-花蓮店"; }
                        if (kw.IndexOf("墾丁") > -1 || kw.IndexOf("屏東") > -1 || kw.IndexOf("船帆") > -1 || kw.IndexOf("恆春") > -1) { kw = "福容大飯店-墾丁店"; }
                        if (kw.IndexOf("貢寮") > -1 || kw.IndexOf("福隆街") > -1) { kw = "福容大飯店-福隆店"; }
                    }
                    if (kw.IndexOf("雀客") > -1 && (kw.IndexOf("旅館") > -1 || kw.IndexOf("快捷") > -1 || kw.IndexOf("藏居") > -1 || kw.IndexOf("旅店") > -1 || kw.IndexOf("飯店") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("台中一中") > -1 || kw.IndexOf("錦新街") > -1) { kw = "雀客快捷台中一中"; isC = true; }
                        if (!isC && (kw.IndexOf("逢甲") > -1 || kw.IndexOf("至善路") > -1)) { kw = "雀客快捷台中逢甲"; isC = true; }
                        if (!isC && kw.IndexOf("二館") == -1 && kw.IndexOf("福星西路") == -1 && (kw.IndexOf("福星") > -1 || kw.IndexOf("福星北一街") > -1)) { kw = "雀客快捷台中福星"; isC = true; }
                        if (!isC && (kw.IndexOf("福星二館") > -1 || kw.IndexOf("福星北三街") > -1)) { kw = "雀客快捷-台中福星二館"; isC = true; }
                        if (!isC && (kw.IndexOf("台中中山") > -1 || kw.IndexOf("市府路") > -1)) { kw = "雀客旅館台中中山"; isC = true; }
                        if (!isC && (kw.IndexOf("台中自由") > -1 || kw.IndexOf("自由路") > -1)) { kw = "雀客旅館台中自由"; isC = true; }
                        if (!isC && (kw.IndexOf("台中青海") > -1 || kw.IndexOf("青海南街") > -1)) { kw = "雀客旅館台中青海"; isC = true; }
                        if (!isC && (kw.IndexOf("台中黎明") > -1 || kw.IndexOf("福星西路") > -1)) { kw = "雀客旅館台中黎明"; isC = true; }
                        if (!isC && (kw.IndexOf("文心") > -1 || kw.IndexOf("中清") > -1 || kw.IndexOf("北平路") > -1)) { kw = "雀客旅馆台中文心中清"; isC = true; }
                        if (!isC && (kw.IndexOf("台中大墩") > -1 || kw.IndexOf("大墩二十街") > -1)) { kw = "雀客藏居台中大墩"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1) { kw = "雀客旅館台中青海"; isC = true; }

                        if (!isC && kw.IndexOf("台南") == -1 && kw.IndexOf("永康區") == -1 && (kw.IndexOf("永康") > -1 || kw.IndexOf("信義路二段") > -1)) { kw = "雀客快捷台北永康"; isC = true; }
                        if (!isC && (kw.IndexOf("台北車站") > -1 || kw.IndexOf("太原路") > -1 || kw.IndexOf("北車") > -1)) { kw = "雀客快捷台北車站"; isC = true; }
                        if (!isC && (kw.IndexOf("內湖") > -1 || kw.IndexOf("民權東路") > -1)) { kw = "雀客旅館台北內湖"; isC = true; }
                        if (!isC && (kw.IndexOf("站前") > -1 || kw.IndexOf("襄陽路") > -1)) { kw = "雀客旅館台北站前"; isC = true; }
                        if (!isC && (kw.IndexOf("南港") > -1 || kw.IndexOf("重陽路") > -1)) { kw = "雀客藏居台北南港"; isC = true; }
                        if (!isC && (kw.IndexOf("陽明山") > -1 || kw.IndexOf("格致路") > -1 || kw.IndexOf("士林") > -1 || kw.IndexOf("溫泉") > -1)) { kw = "雀客藏居台北陽明山"; isC = true; }
                        if (!isC && kw.IndexOf("松江") > -1) { kw = "雀客旅館台北松江"; isC = true; }
                        if (!isC && kw.IndexOf("信義") > -1) { kw = "雀客旅館台北信義"; isC = true; }
                        if (!isC && kw.IndexOf("南京") > -1) { kw = "雀客旅館台北南京"; isC = true; }
                        if (!isC && kw.IndexOf("台北") > -1) { kw = "雀客旅館台北松江"; isC = true; }

                        if (!isC && (kw.IndexOf("淡水") > -1 || kw.IndexOf("中山路") > -1)) { kw = "雀客快捷新北淡水"; isC = true; }
                        if (!isC && (kw.IndexOf("蘆洲") > -1 || kw.IndexOf("集賢路") > -1)) { kw = "雀客旅館新北蘆洲"; isC = true; }
                        if (!isC && (kw.IndexOf("三重") > -1 || kw.IndexOf("捷運路") > -1)) { kw = "雀客藏居新北三重水漾"; isC = true; }
                        if (!isC && (kw.IndexOf("高雄") > -1 || kw.IndexOf("愛河") > -1 || kw.IndexOf("七賢三路") > -1 || kw.IndexOf("鹽埕") > -1)) { kw = "雀客快捷-高雄愛河館"; isC = true; }
                        if (!isC && (kw.IndexOf("台南") > -1 || kw.IndexOf("中正北路") > -1)) { kw = "雀客藏居台南永康"; isC = true; }
                    }
                    if (kw.IndexOf("雅樂軒") > -1 && (kw.IndexOf("飯店") > -1 || kw.IndexOf("酒店") > -1))
                    {
                        if (kw.IndexOf("中山") > -1 || kw.IndexOf("雙城街") > -1) { kw = "中山雅樂軒飯店"; }
                        if (kw.IndexOf("北投") > -1 || kw.IndexOf("大業路") > -1) { kw = "台北北投雅樂軒酒店"; }
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("安平") > -1 || kw.IndexOf("光州路") > -1) { kw = "台南安平雅樂軒酒店"; }
                    }
                    if ((kw.IndexOf("富信") > -1 && kw.IndexOf("飯店") > -1) || (kw.IndexOf("台南") > -1 && kw.IndexOf("鼎園") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("成功路") > -1) { kw = "台南富信大飯店"; isC = true; }
                        if (kw.IndexOf("台中") > -1 || kw.IndexOf("市府路") > -1) { kw = "台中富信大飯店"; isC = true; }
                        if (!isC) { kw = "富信大飯店"; }
                    }
                    if (kw.IndexOf("城市商旅") > -1)
                    {
                        if (kw.IndexOf("真愛") > -1 || kw.IndexOf("大義街") > -1) { kw = "城市商旅-真愛館"; }
                        if (kw.IndexOf("駁二") > -1 || kw.IndexOf("公園二路") > -1) { kw = "城市商旅-駁二館"; }
                        if (kw.IndexOf("南東館") > -1 || kw.IndexOf("南京東路") > -1 || kw.IndexOf("松山") > -1) { kw = "城市商旅-台北南東館"; }
                        if (kw.IndexOf("南西館") > -1 || kw.IndexOf("南京西路") > -1) { kw = "城市商旅-台北南西館"; }
                        if (kw.IndexOf("北門館") > -1 || kw.IndexOf("長安西路") > -1) { kw = "城市商旅-北門館"; }
                        if (kw.IndexOf("五權館") > -1 || kw.IndexOf("台中") > -1 || kw.IndexOf("五權路") > -1) { kw = "城市商旅-台中五權館"; }
                        if (kw.IndexOf("站前館") > -1 || kw.IndexOf("延平北路") > -1) { kw = "城市商旅-站前館"; }
                        if (kw.IndexOf("車站館") > -1 || kw.IndexOf("中正路") > -1) { kw = "城市商旅-桃園車站館"; }
                        if (kw.IndexOf("航空館") > -1 || kw.IndexOf("中正東路") > -1) { kw = "城市商旅-桃園航空館"; }
                        if (kw.IndexOf("莊二館") > -1 || kw.IndexOf("衡陽路") > -1) { kw = "城市商旅-德立莊二館"; }
                        if (kw.IndexOf("莊分館") > -1 || kw.IndexOf("秀山街") > -1) { kw = "城市商旅-德立莊分館"; }
                    }
                    if (kw.IndexOf("微風") > -1)
                    {
                        if (kw.IndexOf("市民大道") > -1) { kw = "微風廣場-市民大道四段側"; }
                        if (kw.IndexOf("微風忠孝") > -1 || kw.IndexOf("忠孝微風") > -1 || (kw.IndexOf("微風廣場") > -1 && kw.IndexOf("忠孝") > -1 && kw.IndexOf("忠孝東路") == -1)) { kw = "微風廣場-忠孝館"; }
                        if (kw.IndexOf("微風松高") > -1 || kw.IndexOf("松高微風") > -1 || kw.IndexOf("松高路") > -1 || (kw.IndexOf("微風廣場") > -1 && kw.IndexOf("松高") > -1)) { kw = "微風廣場-松高館"; }
                        if (kw.IndexOf("微風信義") > -1 || kw.IndexOf("信義微風") > -1 || (kw.IndexOf("微風廣場") > -1 && kw.IndexOf("信義") > -1 && kw.IndexOf("忠孝東路") == -1)) { kw = "微風廣場-信義館"; }
                        if (kw.IndexOf("微風南山") > -1 || kw.IndexOf("南山微風") > -1 || (kw.IndexOf("微風廣場") > -1 && kw.IndexOf("南山") > -1)) { kw = "微風廣場-南山館"; }
                        if (kw.IndexOf("微風南京") > -1 || kw.IndexOf("南京微風") > -1 || kw.IndexOf("南京東路") > -1 || (kw.IndexOf("微風廣場") > -1 && kw.IndexOf("南京") > -1)) { kw = "微風廣場-南京館"; }
                    }
                    #endregion

                    #region 醫院
                    if ((kw.IndexOf("長庚醫院") > -1 || kw.IndexOf("長庚紀念醫院") > -1 || kw.IndexOf("長庚分院") > -1) && kw.IndexOf("捷運") == -1 && kw.IndexOf("機捷") == -1)
                    {
                        if (kw.IndexOf("院前二路") > -1) { kw = "長庚醫院-院前二路出入口"; }
                        if (kw.IndexOf("復健大樓") > -1) { kw = "長庚醫院-復健大樓出入口"; }
                        if (kw.IndexOf("醫學大樓") > -1 && kw.IndexOf("高雄") == -1) { kw = "長庚醫院-醫學大樓出入口"; }
                        if (kw.IndexOf("桃園") > -1 && kw.IndexOf("林口") == -1) { kw = "桃園長庚紀念醫院"; }
                        if (kw.IndexOf("林口") > -1 && kw.IndexOf("兒童") > -1 && kw.IndexOf("醫學大樓") == -1 && kw.IndexOf("病理大樓") == -1) { kw = "林口長庚醫院兒童大樓"; }
                        if (kw.IndexOf("林口") > -1 && kw.IndexOf("兒童") == -1 && kw.IndexOf("醫學大樓") == -1 && kw.IndexOf("病理大樓") == -1) { kw = "林口長庚醫院"; }
                        if (kw.IndexOf("林口") > -1 && (kw.IndexOf("醫學大樓") > -1 || kw.IndexOf("病理大樓") > -1)) { kw = "林口長庚醫院病理大樓"; }
                        if (kw.IndexOf("高雄") > -1 && kw.IndexOf("醫學大樓") > -1) { kw = "高雄長庚紀念醫院醫學大樓"; }
                        if (kw.IndexOf("高雄") > -1 && kw.IndexOf("醫學大樓") == -1) { kw = "高雄長庚醫院"; }
                        if (kw.IndexOf("台北") > -1) { kw = "台北長庚紀念醫院"; }
                        if (kw.IndexOf("基隆") > -1 && kw.IndexOf("基金一路") > -1) { kw = "基隆長庚醫院情人湖院區"; }
                        if ((kw.IndexOf("基隆") > -1 || kw.IndexOf("麥金路") > -1) && kw.IndexOf("情人湖院區") == -1) { kw = "基隆長庚紀念醫院"; }
                        if (kw.IndexOf("雲林") > -1 || kw.IndexOf("麥寮") > -1 || kw.IndexOf("工業路") > -1) { kw = "雲林長庚紀念醫院"; }
                        if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("急診") > -1) { kw = "嘉義長庚紀念醫院急診"; }
                        if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("急診") == -1) { kw = "嘉義長庚紀念醫院"; }
                        if ((kw.IndexOf("兒童門診") > -1 || kw.IndexOf("兒童") > -1) && kw.IndexOf("高雄") == -1) { kw = "林口長庚醫院兒童大樓"; }
                        if ((kw.IndexOf("兒童門診") > -1 || kw.IndexOf("兒童") > -1) && kw.IndexOf("高雄") > -1) { kw = "高雄長庚紀念醫院兒童大樓"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("長庚醫院") == -1 && kw.IndexOf("林口長庚") > -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("機捷") == -1)
                    {
                        kw = "林口長庚醫院";
                    }
                    if (kw.IndexOf("桃園長庚") > -1 && (kw.IndexOf("門口") > -1 || kw.IndexOf("頂湖") > -1 || kw.IndexOf("門診") > -1) && kw.IndexOf("醫院") == -1)
                    {
                        kw = "桃園長庚紀念醫院"; isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("壢新國際醫院") > -1 || kw.IndexOf("壢新醫院") > -1) { kw = "聯新國際醫院"; }
                    if (kw.IndexOf("平鎮") > -1 && kw.IndexOf("聯新") > -1 && kw.IndexOf("醫院") > -1 && kw.IndexOf("分院") == -1) { kw = "聯新國際醫院"; }
                    if (kw.IndexOf("聯新") > -1 && kw.IndexOf("桃新") > -1 && kw.IndexOf("分院") == -1) { kw = "聯新國際醫院桃新分院"; }
                    if (kw.IndexOf("部桃醫院") > -1 || kw.IndexOf("省桃醫院") > -1 || (kw.IndexOf("部桃") > -1 && kw.IndexOf("門診") > -1)) { kw = "桃園醫院"; isReplaceMarkName = true; }
                    if (kw.IndexOf("新樓醫院") > -1 && kw.IndexOf("急診") == -1 && kw.IndexOf("麻豆") == -1 && kw.IndexOf("安南") == -1) { kw = "台南新樓醫院"; isReplaceMarkName = true; }
                    if (kw.IndexOf("新樓醫院") > -1 && kw.IndexOf("急診") > -1 && kw.IndexOf("麻豆") == -1 && kw.IndexOf("安南") == -1) { kw = "台南新樓醫院急診室"; isReplaceMarkName = true; }
                    if (kw.IndexOf("新樓醫院") > -1 && kw.IndexOf("麻豆") > -1 && kw.IndexOf("安南") == -1) { kw = "麻豆新樓醫院"; }
                    if (kw.IndexOf("新樓醫院") > -1 && kw.IndexOf("安南") > -1) { kw = "新樓醫院附安南門診部"; }
                    if (kw.IndexOf("台大") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("分院") > -1 || kw.IndexOf("門診") > -1) && kw.IndexOf("新竹") > -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("竹東") == -1)
                    {
                        kw = "台大醫院-新竹分院";
                    }
                    if (kw.IndexOf("台大") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("分院") > -1 || kw.IndexOf("門診") > -1) && kw.IndexOf("竹東") > -1 && kw.IndexOf("捷運") == -1)
                    {
                        kw = "台大醫院竹東分院附設護理之家";
                    }
                    if (kw.IndexOf("台大醫院雲林分院斗六院區") == -1 && kw.IndexOf("台大") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("分院") > -1 || kw.IndexOf("門診") > -1) && (kw.IndexOf("雲林") > -1 || kw.IndexOf("斗六") > -1) && kw.IndexOf("虎尾") == -1 && kw.IndexOf("捷運") == -1)
                    {
                        kw = "台大醫院雲林分院斗六院區";
                    }
                    if (kw.IndexOf("台大醫院雲林分院虎尾院區") == -1 && kw.IndexOf("台大") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("分院") > -1 || kw.IndexOf("門診") > -1) && kw.IndexOf("虎尾") > -1 && kw.IndexOf("捷運") == -1)
                    {
                        kw = "台大醫院雲林分院虎尾院區";
                    }
                    if (kw.IndexOf("台大") > -1 && (kw.IndexOf("住院") > -1 || kw.IndexOf("醫院") > -1) && kw.IndexOf("大樓") > -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("東址") == -1 && kw.IndexOf("西址") == -1)
                    {
                        kw = "台大醫院-東址主樓";
                    }
                    if (kw == "台大醫院")
                    {
                        kw = "台大醫院-東址主樓"; isReplaceMarkName = true;
                    }
                    else if (kw.IndexOf("台大醫院") > -1 && kw.IndexOf("號") == -1 && kw.IndexOf("捷運") == -1 && kw.IndexOf("分院") == -1 && kw.IndexOf("東址") == -1)
                    {
                        if (kw.IndexOf("東址") > -1)
                        {
                            kw = "台大醫院-東址主樓";
                        }
                        if ((kw.IndexOf("西址") > -1 || kw.IndexOf("舊館") > -1 || kw.IndexOf("舊址") > -1 || kw.IndexOf("舊院") > -1) && kw.IndexOf("門診部") == -1)
                        {
                            kw = "台大醫院西址舊館";
                        }
                        if (kw.IndexOf("急診") > -1)
                        {
                            kw = "台大醫院-急診部";
                        }
                        if (kw.IndexOf("門診") > -1)
                        {
                            kw = "台大醫院舊院區門診部";
                        }
                        if (kw.IndexOf("牙醫") > -1)
                        {
                            kw = "台大醫院-牙醫部";
                        }
                        if (kw.IndexOf("徐州路") > -1)
                        {
                            kw = "台大醫院-徐州路出入口";
                        }
                        if (kw.IndexOf("青島西路") > -1)
                        {
                            kw = "台大醫院-青島西路側";
                        }
                        if (kw.IndexOf("兒童") > -1 && kw.IndexOf("路") == -1)
                        {
                            kw = "台大醫院兒童醫療大樓";
                        }
                        if (kw.IndexOf("新竹") > -1)
                        {
                            kw = "台大醫院-新竹分院";
                        }
                        if (kw.IndexOf("金山") > -1)
                        {
                            kw = "台大醫院金山分院";
                        }
                        if (kw.IndexOf("雲林") > -1 || kw.IndexOf("斗六") > -1)
                        {
                            kw = "台大醫院雲林分院斗六院區";
                        }
                        if (kw.IndexOf("虎尾") > -1)
                        {
                            kw = "台大醫院雲林分院虎尾院區";
                        }
                        isReplaceMarkName = true;
                    }
                    if ((kw.IndexOf("台大") > -1 || kw.IndexOf("台灣大學") > -1) && kw.IndexOf("醫院") > -1 && kw.IndexOf("兒童") > -1 && kw.IndexOf("兒童醫療大樓") == -1)
                    {
                        kw = "台大醫院兒童醫療大樓"; isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("土城醫院") > -1 || kw.IndexOf("土城長庚") > -1)
                    {
                        kw = "新北市立土城醫院";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("內湖") > -1 && kw.IndexOf("門診") > -1 && kw.IndexOf("急診") == -1)
                    {
                        kw = "內湖三總-門診出入口";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("內湖") > -1 && kw.IndexOf("急診") > -1)
                    {
                        kw = "內湖三總-急診出入口";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("內湖") > -1 && kw.IndexOf("急診") == -1 && kw.IndexOf("門診") == -1)
                    {
                        kw = "三軍總醫院內湖院區";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("北投") > -1 && kw.IndexOf("中和街") > -1)
                    {
                        kw = "三軍總醫院北投分院門診處";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("北投") > -1 && kw.IndexOf("中和街") == -1 && kw.IndexOf("門診處") == -1)
                    {
                        kw = "三軍總醫院北投分院";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("澎湖") > -1)
                    {
                        kw = "三軍總醫院澎湖分院";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("汀州") > -1)
                    {
                        kw = "三軍總醫院汀州院區";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && (kw.IndexOf("松山") > -1 || kw.IndexOf("健康路") > -1))
                    {
                        kw = "三軍總醫院松山分院急診室";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && (kw.IndexOf("基隆") > -1 || kw.IndexOf("正榮") > -1) && kw.IndexOf("民診") == -1)
                    {
                        kw = "三軍總醫院附基隆正榮院區急診室";
                    }
                    if ((kw.IndexOf("三總") > -1 || kw.IndexOf("三軍總醫院") > -1) && kw.IndexOf("基隆") > -1 && (kw.IndexOf("民診") > -1 || kw.IndexOf("孝二路") > -1))
                    {
                        kw = "三軍總醫院附基隆民診處孝二院區";
                    }
                    if (kw.IndexOf("台北") == -1 && kw.IndexOf("榮總") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("大樓") > -1 || kw.IndexOf("分院") > -1))
                    {
                        if (kw.IndexOf("醫院") == -1)
                        {
                            kw = kw.Replace("榮總", "榮民總醫院");
                        }
                        else
                        {
                            kw = kw.Replace("榮總", "榮民總");
                        }
                    }
                    if (kw.IndexOf("榮總") > -1 || kw.IndexOf("榮民總醫院") > -1 || kw.IndexOf("榮民醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("思源樓") > -1) { kw = "台北榮總-思源樓"; isC = true; }
                        if (kw.IndexOf("中正樓") > -1) { kw = "台北榮民總醫院-中正樓"; isC = true; }
                        if (kw.IndexOf("長青樓") > -1) { kw = "台北榮總-長青樓"; isC = true; }
                        if (kw.IndexOf("新竹") > -1) { kw = "台北榮總新竹分院"; isC = true; }
                        if (kw.IndexOf("蘇澳") > -1) { kw = "台北榮總蘇澳分院"; isC = true; }
                        if (!isC && kw.IndexOf("鳳林") > -1 && kw.IndexOf("門診") == -1) { kw = "台北榮總鳳林分院"; isC = true; }
                        if (!isC && kw.IndexOf("鳳林") > -1 && kw.IndexOf("門診") > -1) { kw = "台北榮總鳳林分院附設門診部"; isC = true; }
                        if (!isC && kw.IndexOf("台東") > -1 && kw.IndexOf("更生") > -1) { kw = "台北榮總台東更生院區"; isC = true; }
                        if (!isC && kw.IndexOf("台東") > -1 && kw.IndexOf("勝利") > -1) { kw = "台北榮總台東分院勝利院區"; isC = true; }
                        if (!isC && kw.IndexOf("花蓮") > -1 && kw.IndexOf("玉里") > -1 && kw.IndexOf("長良") == -1 && kw.IndexOf("鳳林") == -1) { kw = "台北榮總玉里分院大門"; isC = true; }
                        if (!isC && kw.IndexOf("花蓮") > -1 && kw.IndexOf("玉里") > -1 && kw.IndexOf("長良") > -1 && kw.IndexOf("鳳林") == -1) { kw = "台北榮總玉里長良園區"; isC = true; }
                        if (!isC && (kw.IndexOf("宜蘭") > -1 || kw.IndexOf("員山") > -1) && kw.IndexOf("門診") == -1 && kw.IndexOf("蘇澳") == -1) { kw = "台北榮總員山分院"; isC = true; }
                        if (!isC && (kw.IndexOf("宜蘭") > -1 || kw.IndexOf("員山") > -1) && kw.IndexOf("門診") > -1 && kw.IndexOf("蘇澳") == -1) { kw = "台北榮總員山分院附門診"; isC = true; }
                        if (kw.IndexOf("永康") > -1 || kw.IndexOf("台南") > -1) { kw = "高雄榮民總醫院台南分院"; isC = true; }
                        if (!isC && kw.IndexOf("高雄") > -1 && kw.IndexOf("榮總路") == -1) { kw = "高雄榮民總醫院"; isC = true; }
                        if (!isC && kw.IndexOf("桃園") > -1) { kw = "台北榮民總醫院桃園分院"; isC = true; }
                        if (!isC && kw.IndexOf("屏東") > -1) { kw = "屏東榮民總醫院"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1 || kw.IndexOf("急診") > -1) { kw = "台中榮民總醫院急診"; isC = true; }
                        if (!isC && kw.IndexOf("南投") > -1 || kw.IndexOf("埔里") > -1) { kw = "台中榮民總醫院埔里分院"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1 && kw.IndexOf("第一") > -1) { kw = "台中榮民總醫院第一醫療大樓"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1 && kw.IndexOf("第二") > -1) { kw = "台中榮民總醫院第二醫療大樓"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1 && kw.IndexOf("婦幼") > -1) { kw = "台中榮民總醫院婦幼大樓"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1 && kw.IndexOf("婦幼") == -1 && kw.IndexOf("第一") == -1 && kw.IndexOf("第二") == -1) { kw = "台中榮民總醫院"; isC = true; }
                        if (!isC && kw.IndexOf("竹崎") > -1 || kw.IndexOf("鹿滿") > -1) { kw = "台中榮民總醫院灣橋分院鹿滿院區"; isC = true; }
                        if (!isC && kw.IndexOf("嘉義") > -1 && kw.IndexOf("鹿滿") == -1) { kw = "嘉義榮民總醫院"; isC = true; }
                        if (!isC && kw.IndexOf("致德") > -1) { kw = "台北榮總致德樓"; isC = true; }
                        if (!isC && kw.IndexOf("石牌") > -1 && kw.IndexOf("第三") > -1) { kw = "台北榮總第三門診"; isC = true; }
                        if (!isC && (kw.IndexOf("台北") > -1 || kw.IndexOf("北投") > -1 || kw.IndexOf("石牌") > -1) && kw.IndexOf("致德樓") == -1) { kw = "台北榮總"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("國泰") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        if (kw.IndexOf("汐止") > -1) { kw = "汐止國泰綜合醫院"; }
                        if (kw.IndexOf("新竹") > -1) { kw = "新竹國泰綜合醫院"; }
                        if ((kw.IndexOf("大安") > -1 || kw.IndexOf("台北") > -1) && kw.IndexOf("第一") == -1 && kw.IndexOf("第二") == -1) { kw = "國泰綜合醫院"; }
                        if ((kw.IndexOf("大安") > -1 || kw.IndexOf("台北") > -1) && kw.IndexOf("第一") > -1) { kw = "國泰綜合醫院第一分館"; }
                        if ((kw.IndexOf("大安") > -1 || kw.IndexOf("台北") > -1) && kw.IndexOf("第二") > -1) { kw = "國泰綜合醫院第二分館"; }
                        if (kw.IndexOf("板橋") > -1 && kw.IndexOf("綜合") == -1) { kw = "國泰醫院"; }
                    }

                    if (kw.IndexOf("光田綜合醫院") > -1) { kw = kw.Replace("光田綜合醫院", "光田醫院"); }
                    if (kw.IndexOf("光田醫院") > -1 && kw.IndexOf("急診") > -1 && kw.IndexOf("大甲") == -1 && kw.IndexOf("通霄") == -1) { kw = "光田醫院急診室"; }
                    if (kw.IndexOf("光田醫院") > -1 && kw.IndexOf("急診") == -1 && kw.IndexOf("大甲") == -1 && kw.IndexOf("通霄") == -1) { kw = "光田醫院"; }
                    if (kw.IndexOf("光田醫院") > -1 && kw.IndexOf("大甲") > -1) { kw = "光田醫院大甲院區"; }
                    if (kw.IndexOf("光田醫院") > -1 && kw.IndexOf("通霄") > -1) { kw = "通霄光田醫院"; }
                    if (kw.IndexOf("奇美醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("佳里") > -1) { kw = "佳里奇美醫院"; isC = true; }
                        if (kw.IndexOf("第一") > -1 || kw.IndexOf("中華路") > -1) { kw = "奇美醫院第一醫療大樓"; isC = true; }
                        if (kw.IndexOf("第三") > -1 || kw.IndexOf("甲頂路") > -1) { kw = "奇美醫院第三醫療大樓"; isC = true; }
                        if (kw.IndexOf("台南分院") > -1 || kw.IndexOf("樹林") > -1) { kw = "奇美醫院台南分院"; isC = true; }
                        if (kw.IndexOf("柳營") > -1 || kw.IndexOf("太康") > -1) { kw = "柳營奇美醫院"; isC = true; }
                        if (!isC) { kw = "奇美醫院"; }
                    }
                    if (kw.IndexOf("花蓮") > -1 && kw.IndexOf("門諾醫院") > -1 && kw.IndexOf("花蓮縣") == -1 && kw.IndexOf("花蓮市") == -1) { kw = "花蓮縣門諾醫院"; }
                    if (kw.IndexOf("豐原") > -1 && kw.IndexOf("省立醫院") > -1) { kw = "豐原醫院"; }
                    if (kw.IndexOf("桃園醫院") > -1 && kw.IndexOf("新屋") == -1) { kw = "桃園市桃園區中山路桃園醫院"; }
                    if (kw.IndexOf("桃園醫院") > -1 && kw.IndexOf("新屋") > -1) { kw = "桃園醫院新屋分院"; }
                    if (kw.IndexOf("仁慈醫院") > -1) { kw = "仁慈醫院"; }
                    if (kw.IndexOf("馬偕") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("急診") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("淡水") > -1 && kw.IndexOf("恩典樓") > -1) { kw = "馬偕醫院淡水院區恩典樓"; isC = true; }
                        if (kw.IndexOf("淡水") > -1 && kw.IndexOf("圖書館") > -1) { kw = "馬偕紀念醫院淡水院區圖書館"; isC = true; }
                        if (kw.IndexOf("淡水") > -1 && kw.IndexOf("恩典樓") == -1 && kw.IndexOf("圖書館") == -1) { kw = "馬偕醫院淡水院區"; isC = true; }
                        if (kw.IndexOf("台北") > -1) { kw = "馬偕紀念醫院台北院區"; isC = true; }
                        if (kw.IndexOf("台東") > -1) { kw = "馬偕紀念醫院台東分院"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 && kw.IndexOf("急診") > -1) { kw = "馬偕紀念醫院新竹分院急診"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 && kw.IndexOf("兒童") > -1) { kw = "新竹市立馬偕兒童醫院"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 && kw.IndexOf("急診") == -1 && kw.IndexOf("兒童") == -1) { kw = "馬偕紀念醫院新竹分院"; isC = true; }
                        if (!isC && kw.IndexOf("急診") > -1) { kw = "馬偕紀念醫院台北院區-急診"; }
                        if (!isC) { kw = "馬偕紀念醫院台北院區"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("淡水馬偕") > -1 && kw.IndexOf("醫院") == -1) { kw = "馬偕醫院淡水院區"; }
                    if (kw.IndexOf("台北馬偕") > -1 && kw.IndexOf("醫院") == -1) { kw = "馬偕紀念醫院台北院區"; }
                    if (kw.IndexOf("台東馬偕") > -1 && kw.IndexOf("醫院") == -1) { kw = "馬偕紀念醫院台東分院"; }
                    if (kw.IndexOf("新竹馬偕") > -1 && kw.IndexOf("醫院") == -1) { kw = "馬偕紀念醫院新竹分院"; }
                    if (kw.IndexOf("成大醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("東豐路") > -1) { kw = "成大醫院-東豐路出入口"; isC = true; }
                        if (kw.IndexOf("小東路") > -1) { kw = "成大醫院-門診大樓小東路出入口"; isC = true; }
                        if (kw.IndexOf("勝利路") > -1) { kw = "成大醫院-門診大樓勝利路出入口"; isC = true; }
                        if (kw.IndexOf("精神") > -1 || kw.IndexOf("復健") > -1) { kw = "成大醫院精神復健中心"; isC = true; }
                        if (kw.IndexOf("醫護") > -1) { kw = "成大醫院-醫護大樓"; isC = true; }
                        if (kw.IndexOf("急診") > -1) { kw = "成大醫院-急診部"; isC = true; }
                        if (kw.IndexOf("斗六") > -1 || kw.IndexOf("雲林") > -1 || kw.IndexOf("莊敬路") > -1) { kw = "成大醫院斗六分院"; isC = true; }
                        if (!isC) { kw = "成大醫院"; }
                    }
                    if (kw.IndexOf("童綜合醫院") > -1 || (kw.IndexOf("童醫院") > -1 && kw.IndexOf("兒童") == -1))
                    {
                        if (kw.IndexOf("沙鹿") > -1 || kw.IndexOf("成功西街") > -1) { kw = "童綜合醫院沙鹿院區"; }
                        if (kw.IndexOf("梧棲") > -1 || kw.IndexOf("台灣大道") > -1) { kw = "童綜合醫院梧棲院區"; }
                    }
                    if (kw.IndexOf("和平醫院") > -1 || (kw.IndexOf("聯合醫院") > -1 && (kw.IndexOf("和平院區") > -1 || kw.IndexOf("中華路") > -1)))
                    {
                        var isC = false;
                        if (kw.IndexOf("急診") > -1) { kw = "和平醫院急診大樓"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("和平路") > -1) { kw = "新竹市北區和平醫院"; isC = true; }
                        if (!isC) { kw = "聯合醫院和平院區"; }
                    }
                    if (kw.IndexOf("中興") > -1 && kw.IndexOf("醫院") > -1 && kw.IndexOf("中興路") == -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("三重") > -1) { kw = "新北市三重區中興醫院"; isC = true; }
                        if (kw.IndexOf("南投") > -1) { kw = "南投醫院中興院區"; isC = true; }
                        if (kw.IndexOf("板橋") > -1 && kw.IndexOf("海山") == -1) { kw = "新北市板橋區中興醫院"; isC = true; }
                        if (kw.IndexOf("海山") > -1 || kw.IndexOf("土城") > -1) { kw = "板橋中興醫院海山院區"; isC = true; }
                        if (kw.IndexOf("新竹") > -1 || kw.IndexOf("興南街") > -1) { kw = "新中興醫院"; isC = true; }
                        if (!isC) { kw = "臺北市立聯合醫院中興院區"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("衛生福利部") > -1 && kw.IndexOf("嘉義") > -1 && kw.IndexOf("醫院") > -1) { kw = "嘉義醫院"; isReplaceMarkName = true; }
                    if (kw.IndexOf("國軍") > -1 && kw.IndexOf("桃園") > -1 && kw.IndexOf("總醫院") > -1) { kw = "國軍桃園總醫院"; }
                    if (kw.IndexOf("國軍") > -1 && kw.IndexOf("花蓮") > -1 && kw.IndexOf("總醫院") > -1 && kw.IndexOf("進豐") > -1) { kw = "國軍花蓮總醫院進豐門診處"; }
                    if (kw.IndexOf("國軍") > -1 && kw.IndexOf("花蓮") > -1 && kw.IndexOf("總醫院") > -1 && kw.IndexOf("進豐") == -1) { kw = "國軍花蓮總醫院北埔總院區"; }
                    if (kw.IndexOf("慈濟") > -1 && (kw.IndexOf("醫院") > -1 || kw.IndexOf("急診") > -1 || kw.IndexOf("門診") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("潭子") > -1) { kw = "台中慈濟醫院"; isC = true; }
                        if (kw.IndexOf("台中") > -1 && (kw.IndexOf("急診") > -1 || kw.IndexOf("復健") > -1)) { kw = "台中慈濟醫院"; isC = true; }
                        if (kw.IndexOf("台中") > -1 && kw.IndexOf("第一") > -1) { kw = "台中慈濟醫院-第一院區"; isC = true; }
                        if (kw.IndexOf("台中") > -1 && (kw.IndexOf("第二") > -1 || kw.IndexOf("護理之家") > -1)) { kw = "台中慈濟醫院-第二院區"; isC = true; }
                        if (!isC && kw.IndexOf("台中") > -1) { kw = "台中慈濟醫院"; isC = true; }
                        if (kw.IndexOf("大林") > -1 || kw.IndexOf("嘉義") > -1) { kw = "大林慈濟醫院"; isC = true; }
                        if (kw.IndexOf("斗六") > -1 || kw.IndexOf("雲林") > -1) { kw = "斗六慈濟醫院"; isC = true; }
                        if (kw.IndexOf("關山") > -1 || kw.IndexOf("台東") > -1) { kw = "關山慈濟醫院"; isC = true; }
                        if (kw.IndexOf("玉里") > -1 || (kw.IndexOf("花蓮") > -1 && kw.IndexOf("民權街") > -1)) { kw = "玉里慈濟醫院"; isC = true; }
                        if (kw.IndexOf("花蓮") > -1) { kw = "花蓮慈濟醫院"; isC = true; }
                        if ((kw.IndexOf("台北") > -1 || kw.IndexOf("新店") > -1) && kw.IndexOf("急診") > -1) { kw = "台北慈濟醫院-急診部"; isC = true; }
                        if (!isC) { kw = "台北慈濟醫院"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("空軍總醫院") > -1) { kw = "三總松山分院"; }
                    if (kw.IndexOf("台大生醫") > -1 && (kw.IndexOf("竹北") > -1 || kw.IndexOf("生醫路") > -1)) { kw = "台大生醫醫院竹北院區"; }
                    if (kw.IndexOf("台大生醫") > -1 && (kw.IndexOf("竹東") > -1 || kw.IndexOf("至善路") > -1)) { kw = "台大生醫醫院竹東院區"; }
                    if (kw.IndexOf("台大生醫") > -1 && kw.IndexOf("竹東") == -1 && kw.IndexOf("竹北") == -1) { kw = "台大生醫醫院竹北院區"; }
                    if (kw.IndexOf("阮綜合") > -1 && kw.IndexOf("牙科") > -1) { kw = "阮綜合醫院牙科部"; }
                    if (kw.IndexOf("阮綜合") > -1 && kw.IndexOf("牙科") == -1) { kw = "阮綜合醫院"; }
                    if (kw.IndexOf("澄清醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("本堂") > -1 || kw.IndexOf("中正路") > -1 || kw.IndexOf("霧峰區") > -1) { kw = "本堂澄清醫院"; isC = true; }
                        if (kw.IndexOf("烏日") > -1 || kw.IndexOf("光明路") > -1) { kw = "烏日澄清醫院"; isC = true; }
                        if (kw.IndexOf("太平") > -1 || kw.IndexOf("中興路") > -1) { kw = "新太平澄清醫院"; isC = true; }
                        if (kw.IndexOf("敬義") > -1 || kw.IndexOf("福康路") > -1) { kw = "澄清醫院中港院區敬義樓"; isC = true; }
                        if (kw.IndexOf("平等") > -1 && kw.IndexOf("急診") > -1) { kw = "澄清醫院平等院區急診室"; isC = true; }
                        if (kw.IndexOf("平等") > -1 && kw.IndexOf("急診") == -1) { kw = "澄清醫院平等院區"; isC = true; }
                        if ((kw.IndexOf("霧峰") > -1 && kw.IndexOf("霧峰區") == -1) || kw.IndexOf("大里") > -1 || kw.IndexOf("成功路") > -1) { kw = "霧峰澄清醫院"; isC = true; }
                        if (!isC) { kw = "澄清醫院中港院區"; }
                    }
                    if (kw.IndexOf("新北市") > -1 && kw.IndexOf("林口區") > -1 && kw.IndexOf("衛生所") > -1) { kw = "林口區衛生所"; }
                    if (kw.IndexOf("禾馨") > -1 && (kw.IndexOf("診所") > -1 || kw.IndexOf("婦產科") > -1 || kw.IndexOf("專科") > -1 || kw.IndexOf("護理") > -1))
                    {
                        var isC = false;
                        if (kw.IndexOf("小兒") > -1 && kw.IndexOf("士林") > -1) { kw = "小禾馨士林小兒專科診所"; isC = true; }
                        if (kw.IndexOf("小兒") > -1 && kw.IndexOf("民權") > -1) { kw = "小禾馨民權小兒專科診所"; isC = true; }
                        if (kw.IndexOf("小兒") > -1 && kw.IndexOf("懷寧") > -1) { kw = "小禾馨懷寧小兒專科診所-"; isC = true; }
                        if (kw.IndexOf("小兒") > -1 && !isC) { kw = "小禾馨兒童專科"; isC = true; }
                        if (kw.IndexOf("士林") > -1 && kw.IndexOf("產後") > -1) { kw = "禾馨士林產後護理之家"; isC = true; }
                        if (kw.IndexOf("內湖") > -1 && !isC) { kw = "禾馨內湖婦幼診所-"; isC = true; }
                        if (kw.IndexOf("新生") > -1 && !isC) { kw = "禾馨新生婦幼診所"; isC = true; }
                        if (kw.IndexOf("民權") > -1 && kw.IndexOf("民權東路") > -1 && !isC) { kw = "禾馨民權婦幼診所-民權東路出入口"; isC = true; }
                        if (kw.IndexOf("民權") > -1 && kw.IndexOf("行忠路") > -1 && !isC) { kw = "禾馨民權婦幼診所-行忠路出入口"; isC = true; }
                        if (kw.IndexOf("民權") > -1 && !isC) { kw = "禾馨民權婦幼診所"; isC = true; }
                        if (kw.IndexOf("生殖中心") > -1 && kw.IndexOf("桃園") == -1 && !isC) { kw = "禾馨宜蘊生殖中心-桃園店"; isC = true; }
                        if (kw.IndexOf("生殖中心") > -1 && !isC) { kw = "禾馨宜蘊生殖中心-台北店"; isC = true; }
                        if (kw.IndexOf("宜蘊") > -1 && kw.IndexOf("台中") == -1 && !isC) { kw = "禾馨宜蘊婦產科診所-台中店"; isC = true; }
                        if (kw.IndexOf("宜蘊") > -1 && !isC) { kw = "禾馨宜蘊婦產科診所-"; isC = true; }
                        if (kw.IndexOf("桃園") > -1 && kw.IndexOf("外科") == -1) { kw = "禾馨桃園外科診所-"; isC = true; }
                        if (kw.IndexOf("桃園") > -1 && !isC) { kw = "禾馨桃園婦幼診所"; isC = true; }
                        if (kw.IndexOf("眼科") > -1 && !isC) { kw = "禾馨眼科診所"; isC = true; }
                        if ((kw.IndexOf("聯合") > -1 || kw.IndexOf("台南") > -1)) { kw = "禾馨聯合診所"; isC = true; }
                        if (!isC) { kw = "禾馨婦產科診所"; }
                    }
                    if (kw.IndexOf("三總急診") > -1) { kw = "內湖三總-急診出入口"; }
                    if (kw.IndexOf("宋俊宏") > -1 && (kw.IndexOf("婦產科") > -1 || kw.IndexOf("醫院") > -1)) { kw = "宋俊宏婦幼醫院"; }
                    if (kw.IndexOf("台南醫院") > -1 && kw.IndexOf("新化") == -1) { kw = "台南醫院"; }
                    if (kw.IndexOf("台南醫院") > -1 && kw.IndexOf("新化") > -1) { kw = "台南醫院新化分院"; isReplaceMarkName = true; }
                    if (kw.IndexOf("耕莘") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("永和") > -1 && (kw.IndexOf("分院") > -1 || kw.IndexOf("國光路") > -1 || kw.IndexOf("住院大樓") > -1) && kw.IndexOf("中興街") == -1) { kw = "耕莘醫院永和分院住院大樓"; isC = true; }
                        if (!isC && (kw.IndexOf("永和") > -1 || kw.IndexOf("中興街") > -1)) { kw = "永和耕莘醫院"; isC = true; }
                        if (!isC && (kw.IndexOf("安康") > -1 || kw.IndexOf("車子路") > -1)) { kw = "耕莘醫院安康院區"; isC = true; }
                        if (!isC) { kw = "新店耕莘醫院"; }
                    }
                    if (kw.IndexOf("凱旋") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("大寮") > -1 || kw.IndexOf("百合") > -1 || kw.IndexOf("內坑路") > -1) { kw = "凱旋醫院附設大寮百合園區"; isC = true; }
                        if (!isC && (kw.IndexOf("復健") > -1 || kw.IndexOf("福成街") > -1)) { kw = "凱旋醫院附社區復健"; isC = true; }
                        if (!isC) { kw = "凱旋醫院"; }
                    }
                    if (kw.IndexOf("雙和醫院") > -1 && kw.IndexOf("第二") > -1 && kw.IndexOf("醫療") > -1) { kw = "雙和醫院第二醫療大樓"; }
                    if (kw.IndexOf("雙和醫院") > -1 && kw.IndexOf("第一") > -1 && kw.IndexOf("醫療") > -1) { kw = "雙和醫院第一醫療大樓"; }
                    if (kw.IndexOf("雙和醫院") > -1 && kw.IndexOf("中和") > -1) { kw = "雙和醫院"; }
                    if (kw.IndexOf("亞大醫院") > -1 && (kw.IndexOf("霧峰") > -1 || kw.IndexOf("台中") > -1)) { kw = "亞洲大學附屬醫院"; }
                    if (kw.IndexOf("亞大醫院") > -1 && kw.IndexOf("急診") > -1) { kw = "亞洲大學附屬醫院"; }
                    if (kw.IndexOf("亞洲大學") > -1 && kw.IndexOf("醫院") > -1) { kw = "亞洲大學附屬醫院"; }
                    if (kw.IndexOf("台安") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("新北") > -1 || kw.IndexOf("三芝") > -1) { kw = "新北市三芝區台安醫院"; isC = true; }
                        if (kw.IndexOf("進化") > -1 || kw.IndexOf("東區") > -1) { kw = "台安醫院進化總院"; isC = true; }
                        if (kw.IndexOf("雙十") > -1 || kw.IndexOf("北區") > -1) { kw = "台安醫院雙十分院"; isC = true; }
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("四季") > -1 || kw.IndexOf("三民") > -1) { kw = "四季台安醫院"; isC = true; }
                        if (!isC) { kw = "台北市松山區台安醫院"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("敏盛") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("三民") > -1) { kw = "敏盛綜合醫院三民院區"; isC = true; }
                        if (kw.IndexOf("大園") > -1 || kw.IndexOf("華中街") > -1) { kw = "大園敏盛醫院"; isC = true; }
                        if (kw.IndexOf("龍潭") > -1 || kw.IndexOf("中豐路") > -1) { kw = "龍潭敏盛醫院"; isC = true; }
                        if (!isC) { kw = "經國敏盛綜合醫院"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("杏和") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("礁溪") > -1 || kw.IndexOf("宜蘭") > -1) { kw = "礁溪杏和醫院"; isC = true; }
                        if (!isC) { kw = "杏和醫院"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("仁愛") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("大里") > -1) { kw = "大里仁愛醫院"; isC = true; }
                        if (kw.IndexOf("宜蘭") > -1 || kw.IndexOf("蘭陽") > -1) { kw = "宜蘭仁愛醫院"; isC = true; }
                        if (kw.IndexOf("台南") > -1 || kw.IndexOf("北門路") > -1) { kw = "台南市東區仁愛醫院"; isC = true; }
                        if (kw.IndexOf("新北") > -1 || kw.IndexOf("樹林") > -1) { kw = "新北市樹林區仁愛醫院"; isC = true; }
                        if (kw.IndexOf("急診") > -1) { kw = "仁愛醫院急診室"; isC = true; }
                        if (!isC) { kw = "台北仁愛醫院"; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("基督教") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("高雄") > -1) { kw = "高雄基督教醫院"; isC = true; }
                        if (kw.IndexOf("台東") > -1) { kw = "台東基督教醫院"; isC = true; }
                        if (kw.IndexOf("埔里") > -1) { kw = "埔里基督教醫院"; isC = true; }
                        if (!isC && kw.IndexOf("南投") > -1) { kw = "南投基督教醫院"; isC = true; }
                        if (kw.IndexOf("嘉義") > -1 && kw.IndexOf("保健") > -1) { kw = "嘉義基督教醫院保健大樓"; isC = true; }
                        if (!isC && kw.IndexOf("嘉義") > -1) { kw = "嘉義基督教醫院"; isC = true; }
                        if (kw.IndexOf("瑞光") > -1 || kw.IndexOf("建豐路") > -1) { kw = "屏東基督教醫院瑞光院區"; isC = true; }
                        if (kw.IndexOf("恆春") > -1 || kw.IndexOf("恒西路") > -1) { kw = "恆春基督教醫院"; isC = true; }
                        if (!isC && kw.IndexOf("屏東") > -1) { kw = "屏東基督教醫院"; isC = true; }
                        if (kw.IndexOf("雲林") > -1 || kw.IndexOf("西螺") > -1) { kw = "彰化基督教醫院雲林分院"; isC = true; }
                        if (kw.IndexOf("漢銘") > -1) { kw = "漢銘基督教醫院"; isC = true; }
                        if (kw.IndexOf("員林") > -1 && kw.IndexOf("急診") > -1) { kw = "員林基督教醫院急診室"; isC = true; }
                        if (!isC && kw.IndexOf("員林") > -1) { kw = "員林基督教醫院"; isC = true; }
                        if (kw.IndexOf("二林") > -1 || kw.IndexOf("大成路") > -1) { kw = "二林基督教醫院"; isC = true; }
                        if (kw.IndexOf("長青院區") > -1 || kw.IndexOf("鹿東路") > -1) { kw = "鹿港基督教醫院長青院區"; isC = true; }
                        if (!isC && kw.IndexOf("鹿港") > -1) { kw = "鹿港基督教醫院"; isC = true; }
                        if (kw.IndexOf("向上大樓") > -1) { kw = "彰化基督教醫院向上大樓"; isC = true; }
                        if (kw.IndexOf("中華路") > -1) { kw = "彰化基督教醫院中華路院區"; isC = true; }
                        if (kw.IndexOf("皮膚科") > -1) { kw = "彰化基督教醫院皮膚科-"; isC = true; }
                        if (kw.IndexOf("兒童") > -1) { kw = "彰化基督教醫院兒童醫院"; isC = true; }
                        if (!isC && kw.IndexOf("彰化") > -1) { kw = "彰化基督教醫院"; isC = true; }
                        isReplaceMarkName = true;
                    }
                    if (kw.IndexOf("秀傳") > -1 && kw.IndexOf("醫院") > -1)
                    {
                        var isC = false;
                        if (kw.IndexOf("高雄") > -1 || kw.IndexOf("岡山") > -1) { kw = "岡山醫院"; isC = true; }
                        if (kw.IndexOf("台北") > -1 || kw.IndexOf("光復南路") > -1) { kw = "台北市大安區秀傳醫院"; isC = true; }
                        if (kw.IndexOf("南投") > -1 || kw.IndexOf("竹山") > -1) { kw = "竹山秀傳醫院"; isC = true; }
                        if (kw.IndexOf("彰濱") > -1 || kw.IndexOf("鹿港") > -1) { kw = "彰濱秀傳紀念醫院"; isC = true; }
                        if (kw.IndexOf("光復") > -1) { kw = "秀傳紀念醫院光復分院"; isC = true; }
                        if (kw.IndexOf("延平") > -1) { kw = "秀傳紀念醫院延平大樓"; isC = true; }
                        if (!isC) { kw = "秀傳紀念醫院"; }
                        isReplaceMarkName = true;
                    }
                    #endregion
                }
                #endregion

                #region 有號的捷運   
                if (!isSPMarkName && checkNum > -1 && kw.IndexOf("捷運") > -1 && kw.IndexOf("捷運路") == -1)
                {
                    var newAddr = kw;
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getNewAddr.City)) { newAddr = newAddr.Replace(getNewAddr.City, ""); }
                    if (!string.IsNullOrEmpty(getNewAddr.Dist)) { newAddr = newAddr.Replace(getNewAddr.Dist, ""); }
                    if (!string.IsNullOrEmpty(getNewAddr.Road) && getNewAddr.Road.IndexOf("捷運") > -1 && !string.IsNullOrEmpty(getNewAddr.Num)
                        && reg1.IsMatch(getNewAddr.Num.Replace("號", "")) && int.Parse(getNewAddr.Num.Replace("號", "")) > 20)
                    {
                        newAddr = getNewAddr.Road;  // 忽略門牌號
                    }
                    var arrKw = newAddr.Split("捷運站");
                    var getNum = newAddr;
                    var _getNum = "";
                    if (arrKw.Length == 2 && !string.IsNullOrEmpty(arrKw[0]))
                    {
                        if (!string.IsNullOrEmpty(arrKw[1]))
                        {
                            getNum = arrKw[1];
                        }
                        var getAddrNum = SetGISAddress(new SearchGISAddress { Address = getNum, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getAddrNum.Num))
                        {
                            _getNum = getAddrNum.Num;
                            newAddr = "捷運" + arrKw[0] + "站" + getAddrNum.Num;
                            if (newAddr.IndexOf("號") < newAddr.IndexOf("捷運") && newAddr.IndexOf("號捷運") == -1)
                            {
                                var _kw = newAddr.Replace("出入口", "").Replace("出口", "");
                                var sNum = _kw.IndexOf("號") + 1;
                                var eNum = _kw.Length - (_kw.IndexOf("捷運") - 2);
                                if ((sNum + eNum) < _kw.Length)
                                {
                                    newAddr = "捷運" + _kw.Substring(_kw.IndexOf("號") + 1, _kw.Length - (_kw.IndexOf("捷運") - 2)) + "站" + getAddrNum.Num;
                                }
                            }
                        }
                    }
                    if (arrKw.Length == 1 && newAddr.IndexOf("站") > -1)
                    {
                        getNum = newAddr[(newAddr.IndexOf("站") + 1)..];
                        getNum = getNum.Replace("路口", "路");
                        if (!string.IsNullOrEmpty(getNum))
                        {
                            var getAddrNum = SetGISAddress(new SearchGISAddress { Address = getNum, IsCrossRoads = false });
                            if (!string.IsNullOrEmpty(getAddrNum.Num))
                            {
                                newAddr = newAddr.Substring(0, newAddr.IndexOf("站") + 1) + getAddrNum.Num;
                            }
                        }
                        else
                        {
                            getNum = newAddr.Substring(0, newAddr.IndexOf("捷運"));
                            var getAddrNum = SetGISAddress(new SearchGISAddress { Address = getNum, IsCrossRoads = false });
                            if (!string.IsNullOrEmpty(getAddrNum.Num))
                            {
                                newAddr = newAddr[newAddr.IndexOf("捷運")..] + getAddrNum.Num;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(newAddr))
                    {
                        var _getAddrMark = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                        if (_getAddrMark != null)
                        {
                            newSpeechAddress.Lng_X = _getAddrMark.Lng;
                            newSpeechAddress.Lat_Y = _getAddrMark.Lat;
                            ShowAddr(1, _getAddrMark.Address, newSpeechAddress, ref resAddr, _getAddrMark.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        var getAddrCity = SetGISAddress(new SearchGISAddress { Address = newAddr, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getAddrCity.City) || !string.IsNullOrEmpty(getAddrCity.Dist))
                        {
                            var kw1 = getAddrCity.City + getAddrCity.Dist;
                            var kw2 = newAddr.Replace(kw1, "").Replace("門口", "").Replace("大門", "");
                            var thesaurus = " FORMSOF(THESAURUS," + kw1 + ") and FORMSOF(THESAURUS," + kw2 + ")";
                            _getAddrMark = await GoASRAPI("", thesaurus, markName: "捷運").ConfigureAwait(false);
                            if (_getAddrMark != null)
                            {
                                newSpeechAddress.Lng_X = _getAddrMark.Lng;
                                newSpeechAddress.Lat_Y = _getAddrMark.Lat;
                                ShowAddr(1, _getAddrMark.Address, newSpeechAddress, ref resAddr, _getAddrMark.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    #region 判斷是否有多號
                    var arrN = newAddr.Split("號");
                    var tempNum = "";
                    var tempW = "";
                    foreach (var n in arrN)
                    {
                        var getAddrNum = SetGISAddress(new SearchGISAddress { Address = n, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getAddrNum.Num)) { tempNum = getAddrNum.Num; tempW = n.Replace(tempNum.Replace("號", ""), ""); }
                        if (n.IndexOf("捷運") > -1)
                        {
                            newAddr = n + tempNum;
                            var _getAddrMark = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                            if (_getAddrMark != null)
                            {
                                newSpeechAddress.Lng_X = _getAddrMark.Lng;
                                newSpeechAddress.Lat_Y = _getAddrMark.Lat;
                                ShowAddr(1, _getAddrMark.Address, newSpeechAddress, ref resAddr, _getAddrMark.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            if (!string.IsNullOrEmpty(tempNum))
                            {
                                _getAddrMark = await GoASRAPI("捷運" + tempW.Replace("站", "") + "站" + tempNum, "").ConfigureAwait(false);
                                if (_getAddrMark != null)
                                {
                                    newSpeechAddress.Lng_X = _getAddrMark.Lng;
                                    newSpeechAddress.Lat_Y = _getAddrMark.Lat;
                                    ShowAddr(1, _getAddrMark.Address, newSpeechAddress, ref resAddr, _getAddrMark.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    #endregion
                    var getAddrMark = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                    if (getAddrMark != null)
                    {
                        newSpeechAddress.Lng_X = getAddrMark.Lng;
                        newSpeechAddress.Lat_Y = getAddrMark.Lat;
                        ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    #region 夾帶其他字的捷運
                    if (!string.IsNullOrEmpty(_getNum))
                    {
                        newAddr = newAddr.Replace("捷運站", "").Replace("捷運", "").Replace("出入口", "").Replace("出口", "").Replace(_getNum, "");
                        var tempThesaurusM = "";
                        var kwNum = 5; // 先找5個字的捷運
                        if (newAddr.Length > kwNum)
                        {
                            var tempM = newAddr[kwNum..];
                            for (var i = 0; i < newAddr.Length; i++)
                            {
                                if (i <= (newAddr.Length - kwNum))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + newAddr.Substring(i, kwNum) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(tempThesaurusM))
                            {
                                tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                var thesaurus = " FORMSOF(THESAURUS,捷運) and FORMSOF(THESAURUS," + _getNum + ") and (" + tempThesaurusM + ")";
                                getAddrMark = await GoASRAPI("", thesaurus, isNum: false).ConfigureAwait(false);
                                if (getAddrMark != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrMark.Lng;
                                    newSpeechAddress.Lat_Y = getAddrMark.Lat;
                                    ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        tempThesaurusM = "";
                        kwNum = 4;
                        if (newAddr.Length > kwNum)
                        {
                            var tempM = newAddr[kwNum..];
                            for (var i = 0; i < newAddr.Length; i++)
                            {
                                if (i <= (newAddr.Length - kwNum))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + newAddr.Substring(i, kwNum) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(tempThesaurusM))
                            {
                                tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                var thesaurus = " FORMSOF(THESAURUS,捷運) and FORMSOF(THESAURUS," + _getNum + ") and (" + tempThesaurusM + ")";
                                getAddrMark = await GoASRAPI("", thesaurus, isNum: false).ConfigureAwait(false);
                                if (getAddrMark != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrMark.Lng;
                                    newSpeechAddress.Lat_Y = getAddrMark.Lat;
                                    ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        tempThesaurusM = "";
                        kwNum = 3;
                        if (newAddr.Length > kwNum)
                        {
                            var tempM = newAddr[kwNum..];
                            for (var i = 0; i < newAddr.Length; i++)
                            {
                                if (i <= (newAddr.Length - kwNum))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + newAddr.Substring(i, kwNum) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(tempThesaurusM))
                            {
                                tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                var thesaurus = " FORMSOF(THESAURUS,捷運) and FORMSOF(THESAURUS," + _getNum + ") and (" + tempThesaurusM + ")";
                                getAddrMark = await GoASRAPI("", thesaurus, isNum: false).ConfigureAwait(false);
                                if (getAddrMark != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrMark.Lng;
                                    newSpeechAddress.Lat_Y = getAddrMark.Lat;
                                    ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        tempThesaurusM = "";
                        kwNum = 2;
                        if (newAddr.Length > kwNum)
                        {
                            var tempM = newAddr[kwNum..];
                            for (var i = 0; i < newAddr.Length; i++)
                            {
                                if (i <= (newAddr.Length - kwNum))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + newAddr.Substring(i, kwNum) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(tempThesaurusM))
                            {
                                tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                var thesaurus = " FORMSOF(THESAURUS,捷運) and FORMSOF(THESAURUS," + _getNum + ") and (" + tempThesaurusM + ")";
                                getAddrMark = await GoASRAPI("", thesaurus, isNum: false).ConfigureAwait(false);
                                if (getAddrMark != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrMark.Lng;
                                    newSpeechAddress.Lat_Y = getAddrMark.Lat;
                                    ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    #endregion
                }
                #endregion 有號的捷運

                // 檢查是否有指定轉換的名稱更名了
                if (markNameReplaceSub.Length > 0 && markNameReplaceSub[0] != "|")
                {
                    foreach (var m in markNameReplaceSub)
                    {
                        var arrM = m.Split("|");
                        if (kw.IndexOf(arrM[0]) > -1)
                        {
                            kw = kw.Replace(arrM[0], arrM[1]);
                        }
                    }
                }
                #endregion

                #region 先查一遍特殊地標
                if (!isCrossRoadKW && (kw.Trim().Length <= 5 || isReplaceMarkName || checkNum == -1 || kw.IndexOf("-") > -1))
                {
                    var getAddrCity = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    var getMarkNameOne = await GoASRAPI("", kw, markName: kw).ConfigureAwait(false);
                    if (getMarkNameOne != null)
                    {
                        if (getMarkNameOne.Memo != kw)
                        {
                            var getMarkNameOne1 = await GoASRAPI(kw, "", markName: kw).ConfigureAwait(false);
                            if (getMarkNameOne1 != null && kw == getMarkNameOne.Memo)
                            {
                                newSpeechAddress.Lng_X = getMarkNameOne1.Lng;
                                newSpeechAddress.Lat_Y = getMarkNameOne1.Lat;
                                ShowAddr(1, getMarkNameOne1.Address, newSpeechAddress, ref resAddr, getMarkNameOne1.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            newSpeechAddress.Lng_X = getMarkNameOne.Lng;
                            newSpeechAddress.Lat_Y = getMarkNameOne.Lat;
                            ShowAddr(1, getMarkNameOne.Address, newSpeechAddress, ref resAddr, getMarkNameOne.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }

                    #region 判斷是否為City+地標
                    // 先完整查詢
                    if (!string.IsNullOrEmpty(getAddrCity.City) && kw.IndexOf("ELEVEN") == -1)
                    {
                        var kw1 = getAddrCity.City + getAddrCity.Dist;
                        var kw2 = kw.Replace(kw1, "").Replace("門口", "").Replace("大門", "");
                        if (!string.IsNullOrEmpty(getAddrCity.Road)) { kw2 = kw2.Replace(getAddrCity.Road, ""); }
                        var getMarkName3 = await GoASRAPI(kw2, "", markName: kw1, onlyAddr: kw1).ConfigureAwait(false);
                        if (getMarkName3 != null && getMarkName3.Address.IndexOf(kw1) > -1 && getMarkName3.Memo?.IndexOf(kw2) > -1)
                        {
                            newSpeechAddress.Lng_X = getMarkName3.Lng;
                            newSpeechAddress.Lat_Y = getMarkName3.Lat;
                            ShowAddr(1, getMarkName3.Address, newSpeechAddress, ref resAddr, getMarkName3.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        if (!string.IsNullOrEmpty(kw2))
                        {
                            var thesaurus = " FORMSOF(THESAURUS," + kw1 + ") and FORMSOF(THESAURUS," + kw2 + ")";
                            getMarkName3 = await GoASRAPI("", thesaurus, markName: kw2).ConfigureAwait(false);
                            if (getMarkName3 != null && getMarkName3.Address.IndexOf(kw1) > -1)
                            {
                                newSpeechAddress.Lng_X = getMarkName3.Lng;
                                newSpeechAddress.Lat_Y = getMarkName3.Lat;
                                ShowAddr(1, getMarkName3.Address, newSpeechAddress, ref resAddr, getMarkName3.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(getAddrCity.City))
                    {
                        var newKw = kw.Replace(getAddrCity.City, "");
                        if (!string.IsNullOrEmpty(getAddrCity.City2))
                        {
                            newKw = newKw.Replace(getAddrCity.City2, "");
                        }
                        if (!string.IsNullOrEmpty(getAddrCity.Dist))
                        {
                            newKw = newKw.Replace(getAddrCity.Dist, "");
                        }
                        if (!string.IsNullOrEmpty(getAddrCity.Dist2))
                        {
                            newKw = newKw.Replace(getAddrCity.Dist2, "");
                        }
                        if (!string.IsNullOrEmpty(newKw.Replace("餵", "")))
                        {
                            var thesaurusC = " FORMSOF(THESAURUS," + getAddrCity.City + ") and FORMSOF(THESAURUS," + newKw.Replace("餵", "") + ")";
                            var getMarkName1 = await GoASRAPI("", thesaurusC, markName: newKw, onlyAddr: newKw).ConfigureAwait(false);
                            if (getMarkName1 != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName1.Lng;
                                newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            else
                            {
                                newKw = newKw.Replace("餐廳", "");
                                var _newKw = getAddrCity.City.Remove(getAddrCity.City.Length - 1) + newKw;
                                getMarkName1 = await GoASRAPI(_newKw, "", markName: _newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                getMarkName1 = await GoASRAPI(newKw, "", markName: getAddrCity.City).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var c in allCity)
                        {
                            if (kw.StartsWith(c) && !kw.StartsWith(c + "市"))
                            {
                                var newKw = kw.Replace(c, "");
                                var thesaurusC = " FORMSOF(THESAURUS," + c + "市) and FORMSOF(THESAURUS," + newKw + ")";
                                var getMarkName1 = await GoASRAPI("", thesaurusC, markName: kw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                getMarkName1 = await GoASRAPI("", thesaurusC, markName: newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                getMarkName1 = await GoASRAPI(newKw, "", markName: newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                #region 用組的
                                if (newKw.Length > 7)
                                {
                                    thesaurusC = " FORMSOF(THESAURUS," + c + "市) and ";
                                    var tempThesaurusM = "";
                                    var tempM2 = "";
                                    var kwNum = 7;
                                    if (newKw.Length > kwNum)
                                    {
                                        tempM2 = newKw[kwNum..];
                                        for (var i = 0; i < newKw.Length; i++)
                                        {
                                            if (i <= (newKw.Length - kwNum))
                                            {
                                                tempThesaurusM += " FORMSOF(THESAURUS," + newKw.Substring(i, kwNum) + ") or";
                                            }
                                            else
                                            {
                                                break;
                                            }
                                        }
                                        if (!string.IsNullOrEmpty(tempThesaurusM))
                                        {
                                            tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                            getMarkName1 = await GoASRAPI("", thesaurusC + "(" + tempThesaurusM + ")", isNum: false, markName: tempM2).ConfigureAwait(false);
                                            if (getMarkName1 != null)
                                            {
                                                newSpeechAddress.Lng_X = getMarkName1.Lng;
                                                newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                                ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    kwNum = 6;
                                    tempThesaurusM = "";
                                    if (newKw.Length > kwNum)
                                    {
                                        tempM2 = newKw[kwNum..];
                                        for (var i = 0; i < newKw.Length; i++)
                                        {
                                            if (i <= (newKw.Length - kwNum))
                                            {
                                                tempThesaurusM += " FORMSOF(THESAURUS," + newKw.Substring(i, kwNum) + ") or";
                                            }
                                            else
                                            {
                                                break;
                                            }
                                        }
                                        if (!string.IsNullOrEmpty(tempThesaurusM))
                                        {
                                            tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                            getMarkName1 = await GoASRAPI("", thesaurusC + "(" + tempThesaurusM + ")", isNum: false, markName: tempM2).ConfigureAwait(false);
                                            if (getMarkName1 != null)
                                            {
                                                newSpeechAddress.Lng_X = getMarkName1.Lng;
                                                newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                                ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    kwNum = 5;
                                    tempThesaurusM = "";
                                    if (newKw.Length > kwNum)
                                    {
                                        tempM2 = newKw[kwNum..];
                                        for (var i = 0; i < newKw.Length; i++)
                                        {
                                            if (i <= (newKw.Length - kwNum))
                                            {
                                                tempThesaurusM += " FORMSOF(THESAURUS," + newKw.Substring(i, kwNum) + ") or";
                                            }
                                            else
                                            {
                                                break;
                                            }
                                        }
                                        if (!string.IsNullOrEmpty(tempThesaurusM))
                                        {
                                            tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                            getMarkName1 = await GoASRAPI("", thesaurusC + "(" + tempThesaurusM + ")", isNum: false, markName: tempM2).ConfigureAwait(false);
                                            if (getMarkName1 != null)
                                            {
                                                newSpeechAddress.Lng_X = getMarkName1.Lng;
                                                newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                                ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                            }
                                        }
                                    }
                                }
                                #endregion
                            }
                        }
                        foreach (var c in allCityCounty)
                        {
                            if (kw.StartsWith(c) && !kw.StartsWith(c + "縣"))
                            {
                                var newKw = kw.Replace(c, "");
                                var thesaurusC = " FORMSOF(THESAURUS," + c + "縣) and FORMSOF(THESAURUS," + newKw + ")";
                                var getMarkName1 = await GoASRAPI("", thesaurusC, markName: newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                getMarkName1 = await GoASRAPI(newKw, "", markName: newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        foreach (var c in allCityTwo)
                        {
                            if (kw.StartsWith(c) && !kw.StartsWith(c + "市") && !kw.StartsWith(c + "縣"))
                            {
                                var newKw = kw.Replace(c, "");
                                var thesaurusC = " FORMSOF(THESAURUS," + c + ") and FORMSOF(THESAURUS," + newKw + ")";
                                var getMarkName1 = await GoASRAPI("", thesaurusC, markName: newKw).ConfigureAwait(false);
                                if (getMarkName1 != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName1.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName1.Lat;
                                    ShowAddr(1, getMarkName1.Address, newSpeechAddress, ref resAddr, getMarkName1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    #endregion

                    #region 不用地址拆解，直接特殊地標查詢
                    var getMarkName = await GoASRAPI(kw.Replace("對面", "").Replace("門口", ""), "", markName: kw.Replace("對面", "").Replace("門口", "")).ConfigureAwait(false);
                    resAddr.Address = kw;
                    if (getMarkName != null)
                    {
                        newSpeechAddress.Lng_X = getMarkName.Lng;
                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    if (kw.IndexOf("要到") > -1 && !kw.StartsWith("要到"))
                    {
                        getMarkName = await GoASRAPI(kw.Substring(0, kw.IndexOf("要到")), "").ConfigureAwait(false);
                        if (getMarkName != null)
                        {
                            newSpeechAddress.Lng_X = getMarkName.Lng;
                            newSpeechAddress.Lat_Y = getMarkName.Lat;
                            ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    #endregion
                    if (kw.Trim().Length < 5)
                    {
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題, 2).ConfigureAwait(false);
                    }
                    if (kw.IndexOf("7－ELEVEN") > -1)
                    {
                        if (kw.IndexOf("店") > -1 && kw.IndexOf("門市") == -1) { kw = kw.Replace("店", "門市"); }
                        if (kw.IndexOf("門市") > -1)
                        {
                            var k1 = kw.Split("7－ELEVEN");
                            // 門市在小7後面
                            if (k1.Length > 1 && !string.IsNullOrEmpty(k1[1]) && k1[1].IndexOf("門市") > -1)
                            {
                                kw = "7－ELEVEN" + k1[1].Remove(k1[1].IndexOf("門市")) + "門市";
                                getMarkName = await GoASRAPI(kw, "").ConfigureAwait(false);
                                if (getMarkName != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                            // 門市在前
                            if (kw.IndexOf("門市") < kw.IndexOf("7－ELEVEN"))
                            {
                                var _kw = kw.Substring(kw.IndexOf("門市") - 2, 2);
                                getMarkName = await GoASRAPI("7－ELEVEN-" + _kw + "門市", "").ConfigureAwait(false);
                                if (getMarkName != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #region 改用全文檢索
                        var getAddrNoNum = SetGISAddress(new SearchGISAddress { Address = kw.Replace("7－ELEVEN-", "").Replace("口", "").Replace("的", ""), IsCrossRoads = false });
                        var thesaurus7 = "";
                        if (string.IsNullOrEmpty(getAddrNoNum.City) && string.IsNullOrEmpty(getAddrNoNum.Dist) && !string.IsNullOrEmpty(getAddrNoNum.Road))
                        {
                            foreach (var c in allCity)
                            {
                                var crCity = "市";
                                if (getAddrNoNum.Road == (c + "路") || getAddrNoNum.Road == (c + "街"))
                                {
                                    break;
                                }
                                if (getAddrNoNum.Road.IndexOf(c) > -1)
                                {
                                    var arrCR = getAddrNoNum.Road.Split(c);
                                    if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                    {
                                        getAddrNoNum.Road = arrCR[1];
                                    }
                                    getAddrNoNum.City = c + crCity;
                                    break;
                                }
                            }
                            foreach (var c in allCityCounty)
                            {
                                if (getAddrNoNum.Road == (c + "路") || getAddrNoNum.Road == (c + "街"))
                                {
                                    break;
                                }
                                var crCity = "縣";
                                if (getAddrNoNum.Road.IndexOf(c) > -1)
                                {
                                    var arrCR = getAddrNoNum.Road.Split(c);
                                    if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                    {
                                        getAddrNoNum.Road = arrCR[1];
                                    }
                                    getAddrNoNum.City = c + crCity;
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.City)) { thesaurus7 += " FORMSOF(THESAURUS," + getAddrNoNum.City + ") and"; }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Dist)) { thesaurus7 += " FORMSOF(THESAURUS," + getAddrNoNum.Dist + ") and"; }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Road))
                        {
                            if (getAddrNoNum.Road.Length > 4)
                            {
                                thesaurus7 += " (FORMSOF(THESAURUS," + getAddrNoNum.Road.Substring(getAddrNoNum.Road.Length - 3, 3) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Road.Substring(getAddrNoNum.Road.Length - 4, 4) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Road.Substring(getAddrNoNum.Road.Length - 5, 5) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Road + ")) and";
                            }
                            else
                            {
                                thesaurus7 += " FORMSOF(THESAURUS," + getAddrNoNum.Road + ") and";
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Sect)) { thesaurus7 += " FORMSOF(THESAURUS," + getAddrNoNum.Sect + ") and"; }
                        if (!string.IsNullOrEmpty(getAddrNoNum.Lane))
                        {
                            if (getAddrNoNum.Lane.Length > 4)
                            {
                                thesaurus7 += " (FORMSOF(THESAURUS," + getAddrNoNum.Lane.Substring(getAddrNoNum.Lane.Length - 3, 3) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Lane.Substring(getAddrNoNum.Lane.Length - 4, 4) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Lane.Substring(getAddrNoNum.Lane.Length - 5, 5) + ") or";
                                thesaurus7 += "  FORMSOF(THESAURUS," + getAddrNoNum.Lane + ")) and";
                            }
                            else
                            {
                                thesaurus7 += " FORMSOF(THESAURUS," + getAddrNoNum.Lane + ") and";
                            }
                        }
                        thesaurus7 += " FORMSOF(THESAURUS,7－ELEVEN-)";
                        getMarkName = await GoASRAPI("", thesaurus7).ConfigureAwait(false);
                        if (getMarkName != null)
                        {
                            newSpeechAddress.Lng_X = getMarkName.Lng;
                            newSpeechAddress.Lat_Y = getMarkName.Lat;
                            ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        #endregion
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 2).ConfigureAwait(false);
                    }
                    if (isReplaceMarkName && kw.IndexOf("號") == -1)
                    {
                        // 判斷是否非交叉路口也非巷口等也沒有道路基本單位
                        var isOnlyMarkName = true;
                        var mergedKeyWord = mergedCrossRoadKeyWord.Concat(crossRoadTwoWord).Distinct();
                        foreach (var str in mergedKeyWord)
                        {
                            if (kw.IndexOf(str) > -1)
                            {
                                isOnlyMarkName = false;
                                break;
                            }
                        }
                        if (isOnlyMarkName)
                        {
                            // 純地標就用全文檢索再找一次
                            var tempThesaurusM = "";
                            var tempM2 = "";
                            var kwNum = 6;
                            if (kw.Length > kwNum)
                            {
                                tempM2 = kw[kwNum..];
                                for (var i = 0; i < kw.Length; i++)
                                {
                                    if (i <= (kw.Length - kwNum))
                                    {
                                        tempThesaurusM += " FORMSOF(THESAURUS," + kw.Substring(i, kwNum) + ") or";
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                if (!string.IsNullOrEmpty(tempThesaurusM))
                                {
                                    tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                    getMarkName = await GoASRAPI("", tempThesaurusM, isNum: false, markName: tempM2).ConfigureAwait(false);
                                    if (getMarkName != null)
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                            }
                            kwNum = 5;
                            tempThesaurusM = "";
                            if (kw.Length > kwNum)
                            {
                                tempM2 = kw[kwNum..];
                                for (var i = 0; i < kw.Length; i++)
                                {
                                    if (i <= (kw.Length - kwNum))
                                    {
                                        tempThesaurusM += " FORMSOF(THESAURUS," + kw.Substring(i, kwNum) + ") or";
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                if (!string.IsNullOrEmpty(tempThesaurusM))
                                {
                                    tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                    getMarkName = await GoASRAPI("", tempThesaurusM, isNum: false, markName: tempM2).ConfigureAwait(false);
                                    if (getMarkName != null)
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                            }
                            kwNum = 4;
                            tempThesaurusM = "";
                            if (kw.Length > kwNum)
                            {
                                tempM2 = kw[kwNum..];
                                for (var i = 0; i < kw.Length; i++)
                                {
                                    if (i <= (kw.Length - kwNum))
                                    {
                                        tempThesaurusM += " FORMSOF(THESAURUS," + kw.Substring(i, kwNum) + ") or";
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                                if (!string.IsNullOrEmpty(tempThesaurusM))
                                {
                                    tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                    getMarkName = await GoASRAPI("", tempThesaurusM, isNum: false, markName: tempM2).ConfigureAwait(false);
                                    if (getMarkName != null && getMarkName.Memo.IndexOf("停車場") == -1)
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        //判斷是否為路+特殊地標
                        if (string.IsNullOrEmpty(getAddrCity.Num) && !string.IsNullOrEmpty(getAddrCity.Road))
                        {
                            var _kw = getAddrCity.City + getAddrCity.Dist + getAddrCity.Road + getAddrCity.Sect + getAddrCity.Lane + getAddrCity.Non;
                            var other = kw.Replace(_kw, "");
                            if (other != kw && other.Length > 3)
                            {
                                var thesaurusO = " FORMSOF(THESAURUS," + _kw + ") and FORMSOF(THESAURUS," + other + ")";
                                getMarkName = await GoASRAPI("", thesaurusO).ConfigureAwait(false);
                                if (getMarkName != null)
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                if (other.Length > 4)
                                {
                                    thesaurusO = " FORMSOF(THESAURUS," + _kw + ") and ";
                                    thesaurusO += " (FORMSOF(THESAURUS," + other.Substring(0, 3) + ") or";
                                    thesaurusO += "  FORMSOF(THESAURUS," + other[3..] + "))";
                                    getMarkName = await GoASRAPI("", thesaurusO).ConfigureAwait(false);
                                    if (getMarkName != null)
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                    }
                }
                #endregion

                #region 處理交叉路口
                if (checkNum == -1)
                {
                    var isCrossRoads = false; // 判斷結果是否為交叉路口
                    var crKw = kw;
                    #region 判斷是否有重複的關鍵字
                    foreach (var d in noNumKeyWord)
                    {
                        var idx = crKw.IndexOf(d);
                        if (idx > -1 && crKw.IndexOf(d, idx + 1) > -1)
                        {
                            var c1 = crKw.Substring(0, idx + 1);
                            var c2 = crKw[(idx + 2)..];
                            crKw = c1 + "與" + c2;
                            break;
                        }
                    }
                    #endregion
                    #region 判斷是否為交叉路口 只判斷兩條Road(到段)
                    // 移除判斷不需要的贅字
                    if (crKw.IndexOf("7-ELEVEN") > -1) { crKw = crKw.Replace("7-ELEVEN", ""); }
                    if (crKw.IndexOf("7－ELEVEN-") > -1) { crKw = crKw.Replace("7－ELEVEN-", ""); }
                    foreach (var str in excessWords2)
                    {
                        crKw = crKw.Replace(str, "");
                    }
                    if (crKw.IndexOf("中和區") == -1 && kw.IndexOf("中和") > -1 && kw.IndexOf("中和街") == -1 && kw.IndexOf("中和路") == -1)
                    {
                        var pattern = @"中和.{1}街|中和.{1}路"; var regex = new Regex(pattern);
                        if (!regex.IsMatch(crKw)) { crKw = crKw.Replace("中和", ""); }
                    }

                    #region 找出有連接字

                    var checkJoin = -1;
                    crKw = crKw.Replace("中和區", "").Replace("和平區", "").Replace("永和區", "").Replace("和美鎮", "");
                    foreach (var str in joinWords)
                    {
                        checkJoin = crKw.IndexOf(str);
                        if (checkJoin > -1 && crKw.IndexOf("中和區") == -1 && crKw.IndexOf("和平區") == -1 && crKw.IndexOf("永和區") == -1 && crKw.IndexOf("和美鎮") == -1)
                        {
                            var getJoinWord = false;
                            var _tempW = "";
                            if (str == "和" && roadJoinWord.Any(x => crKw.IndexOf(x) > -1))
                            {
                                // 檢查排除路名後是否還有連接字"和"的
                                getJoinWord = true;
                                var tempW = roadJoinWord.Where(x => crKw.IndexOf(x) > -1).FirstOrDefault();
                                var tempKw = crKw.Replace(tempW, "");
                                _tempW = tempW;
                                if (tempKw.IndexOf(str) == -1)
                                {
                                    checkJoin = -1;
                                    break;
                                }
                            }
                            var tempAddress = crKw.Replace(str, "/");
                            if (getJoinWord && !string.IsNullOrEmpty(_tempW))
                            {
                                var _crKw = crKw.Replace(_tempW, "###");
                                tempAddress = _crKw.Replace(str, "/");
                                tempAddress = tempAddress.Replace("###", _tempW);
                            }
                            // 是否有兩組交叉路口，有的話用後面的
                            if (tempAddress.Split("/").Length > 2)
                            {
                                var r1 = tempAddress.Split("/")[1];
                                foreach (var s in crossRoadKeyWord)
                                {
                                    if (r1.IndexOf(s) > -1)
                                    {
                                        r1 = r1[(r1.IndexOf(s) + s.Length)..];
                                    }
                                }
                                tempAddress = r1 + "/" + tempAddress.Split("/")[2];
                            }

                            var getCRAddr = SetGISAddress(new SearchGISAddress { Address = tempAddress, IsCrossRoads = true });

                            if (crKw.IndexOf("路口") > -1 && !string.IsNullOrEmpty(getCRAddr.Road2) && getCRAddr.Road2.Length > 3 && (getCRAddr.Road2.EndsWith("街路") || getCRAddr.Road2.EndsWith("段路")))
                            {
                                getCRAddr.Road2 = getCRAddr.Road2.Remove(getCRAddr.Road2.Length - 1, 1);
                            }

                            #region 路名有連接字
                            var arrJoinWords = crKw.Split(str);
                            if (arrJoinWords.Length > 2 && !string.IsNullOrEmpty(getCRAddr.Road2))
                            {
                                var _crKw = crKw;
                                foreach (var s in crossRoadKeyWord)
                                {
                                    // 移除交叉路口關鍵字
                                    _crKw = _crKw.Replace(s, "");
                                }
                                var _arrJoinWords = _crKw.Split(str);

                                if (string.IsNullOrEmpty(getCRAddr.Road) && !string.IsNullOrEmpty(getCRAddr.Road2))
                                {
                                    // Road1有
                                    getCRAddr = SetGISAddress(new SearchGISAddress { Address = _arrJoinWords[0] + str + _arrJoinWords[1] + "/" + _arrJoinWords[2], IsCrossRoads = true });
                                }
                                if (!string.IsNullOrEmpty(getCRAddr.Road) && string.IsNullOrEmpty(getCRAddr.Road2))
                                {
                                    // Road2有
                                    getCRAddr = SetGISAddress(new SearchGISAddress { Address = _arrJoinWords[0] + "/" + _arrJoinWords[1] + str + _arrJoinWords[2], IsCrossRoads = true });
                                }
                            }
                            #endregion

                            #region 檢查City+Dist是否都卡在Road裡了
                            if (string.IsNullOrEmpty(getCRAddr.City) && string.IsNullOrEmpty(getCRAddr.Dist) && !string.IsNullOrEmpty(getCRAddr.Road) && getCRAddr.Road.Length > 4)
                            {
                                // 處理 City
                                foreach (var c in allCity)
                                {
                                    var crCity = "市";
                                    if (getCRAddr.Road == (c + "路") || getCRAddr.Road == (c + "街"))
                                    {
                                        break;
                                    }
                                    if (getCRAddr.Road.IndexOf(c + crCity) > -1)
                                    {
                                        var arrCR = getCRAddr.Road.Split(c + crCity);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        getCRAddr.City = c + crCity;
                                        break;
                                    }
                                    if (getCRAddr.Road.IndexOf(c) > -1)
                                    {
                                        var arrCR = getCRAddr.Road.Split(c);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        getCRAddr.City = c + crCity;
                                        break;
                                    }
                                }
                                foreach (var c in allCityCounty)
                                {
                                    if (getCRAddr.Road == (c + "路") || getCRAddr.Road == (c + "街"))
                                    {
                                        break;
                                    }
                                    var crCity = "縣";
                                    if (getCRAddr.Road.IndexOf(c + crCity) > -1)
                                    {
                                        var arrCR = getCRAddr.Road.Split(c + crCity);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        getCRAddr.City = c + crCity;
                                        break;
                                    }
                                    if (getCRAddr.Road.IndexOf(c) > -1)
                                    {
                                        var arrCR = getCRAddr.Road.Split(c);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        getCRAddr.City = c + crCity;
                                        break;
                                    }
                                }
                                foreach (var c in allCityTwo)
                                {
                                    if (getCRAddr.Road == (c + "路") || getCRAddr.Road == (c + "街"))
                                    {
                                        break;
                                    }
                                    if (getCRAddr.Road.IndexOf(c) > -1)
                                    {
                                        if (getCRAddr.Road.IndexOf(c + "縣") > -1)
                                        {
                                            var arrCR = getCRAddr.Road.Split(c + "縣");
                                            if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                            {
                                                getCRAddr.Road = arrCR[1];
                                            }
                                            getCRAddr.City = c + "縣";
                                            break;
                                        }
                                        if (getCRAddr.Road.IndexOf(c + "市") > -1)
                                        {
                                            var arrCR = getCRAddr.Road.Split(c + "市");
                                            if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                            {
                                                getCRAddr.Road = arrCR[1];
                                            }
                                            getCRAddr.City = c + "市";
                                            break;
                                        }
                                        getCRAddr.Road = getCRAddr.Road.Replace(c, "");
                                        break;
                                    }
                                }
                            }
                            if (!string.IsNullOrEmpty(getCRAddr.City) && string.IsNullOrEmpty(getCRAddr.Dist) && !string.IsNullOrEmpty(getCRAddr.Road) && getCRAddr.Road.Length > 4)
                            {
                                // 處理 Dist
                                var idxCR = 0;
                                var idxDist = getCRAddr.Road.IndexOf("區");
                                if (idxDist > 2) { idxCR = idxDist - 2; }
                                var twoWord = !string.IsNullOrEmpty(getCRAddr.Road) ? (getCRAddr.Road.Length > 1 ? getCRAddr.Road.Substring(idxCR, 2) : "") : "";
                                if (!string.IsNullOrEmpty(twoWord))
                                {
                                    var getDists = await GetDists(new DistRequest { City = getCRAddr.City }).ConfigureAwait(false);
                                    var _getDist = getDists.Where(x => x.Substring(0, 2) == twoWord).FirstOrDefault();
                                    if (_getDist != null)
                                    {
                                        var arrCR = getCRAddr.Road.Split(_getDist);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        arrCR = getCRAddr.Road.Split(twoWord);
                                        if (arrCR.Length > 1 && !string.IsNullOrEmpty(arrCR[1]))
                                        {
                                            getCRAddr.Road = arrCR[1];
                                        }
                                        getCRAddr.Dist = _getDist;
                                    }
                                }
                            }
                            crKw = getCRAddr.City + getCRAddr.Dist + getCRAddr.Road + getCRAddr.Sect + "/" + getCRAddr.Road2 + getCRAddr.Sect2;
                            #endregion

                            if (!string.IsNullOrEmpty(getCRAddr.Road) && !string.IsNullOrEmpty(getCRAddr.Road2) &&
                                     (getCRAddr.Road != getCRAddr.Road2 || getCRAddr.Road + getCRAddr.Sect != getCRAddr.Road2 + getCRAddr.Sect2))
                            {
                                if (arrJoinWords.Length == 2)
                                {
                                    crKw = crKw.Replace(str, "/");
                                }
                                else
                                {
                                    crKw = getCRAddr.City + getCRAddr.Dist + getCRAddr.Road + getCRAddr.Sect + "/" + getCRAddr.Road2 + getCRAddr.Sect2;
                                }
                                isCrossRoads = true;
                                break;
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(getCRAddr.Road))
                                {
                                    // 過濾是否有多餘的路名
                                    var arrRoad2 = crKw.Split(getCRAddr.Road);
                                    if (arrRoad2.Length > 2)
                                    {
                                        foreach (var cr in arrRoad2)
                                        {
                                            var _cr = SetGISAddress(new SearchGISAddress { Address = cr, IsCrossRoads = false });
                                            if (!string.IsNullOrEmpty(_cr.Road))
                                            {
                                                crKw = getCRAddr.City + getCRAddr.Dist + getCRAddr.Road + getCRAddr.Sect + "/" + _cr.Road + _cr.Sect;
                                                isCrossRoads = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    #endregion

                    #region 沒有連結字但有交叉路口關鍵字
                    if (checkJoin == -1 && !isCrossRoads && delCrossRoadKeyWord.Any(x => crKw.IndexOf(x) > -1))
                    {
                        var getCRAddr = SetGISAddress(new SearchGISAddress { Address = crKw });
                        if (!string.IsNullOrEmpty(getCRAddr.Num) && string.IsNullOrEmpty(getCRAddr.Road2))
                        {
                            // 檢查是否路被放到號裡
                            var tempNum = getCRAddr.Num.Replace("臨", "").Replace("附", "").Replace("之", "").Replace("號", "");
                            if (tempNum.Any(c => !char.IsDigit(c)))
                            {
                                // 存在非數字的文字
                                getCRAddr.Road2 = new string(Array.FindAll(tempNum.ToCharArray(), c => !char.IsDigit(c)));
                                crKw = getCRAddr.City + getCRAddr.Dist + getCRAddr.Road + getCRAddr.Sect + "/" + getCRAddr.Road2 + getCRAddr.Sect2;
                                isCrossRoads = true;
                            }
                        }
                        if (!string.IsNullOrEmpty(getCRAddr.Sect) && string.IsNullOrEmpty(getCRAddr.Road2))
                        {
                            // 檢查是否路被放到段裡
                            string[] arrRoad = { "路", "道", "街" };
                            var tempSect = getCRAddr.Sect;
                            if (arrRoad.Any(x => tempSect.IndexOf(x) > -1))
                            {
                                var getCRAddr2 = SetGISAddress(new SearchGISAddress { Address = tempSect });
                                crKw = getCRAddr.City + getCRAddr.Dist + getCRAddr.Road + "/" + getCRAddr2.Road + getCRAddr2.Sect;
                                isCrossRoads = true;
                            }
                        }
                    }
                    #endregion

                    #region 移除交叉路口關鍵字
                    foreach (var str in delCrossRoadKeyWord)
                    {
                        var idx = crKw.IndexOf(str);
                        if (idx > -1)
                        {
                            if ((str == "路口" || str == "街路口") && crKw.IndexOf("路路口") == -1)
                            {
                                crKw = crKw.Substring(0, idx + 1);
                                break;
                            }
                            crKw = crKw.Substring(0, idx);
                            break;
                        }
                    }
                    #endregion

                    #region 找出沒連結字 但 有兩條道路單位的
                    // 因為一條路會有巷有弄 所以沒有連結字 就只判斷兩條路
                    if (!isCrossRoads && checkJoin == -1)
                    {
                        var getCRAddr = SetGISAddress(new SearchGISAddress { Address = crKw });
                        if (string.IsNullOrEmpty(getCRAddr.Num))
                        {
                            foreach (var twoWord in crossRoadTwoWord)
                            {
                                var arrKw = crKw.Split(twoWord);
                                if (arrKw.Length >= 2 && !string.IsNullOrEmpty(arrKw[1]))
                                {
                                    var getRoad1 = SetGISAddress(new SearchGISAddress { Address = arrKw[0] + twoWord });
                                    if (!string.IsNullOrEmpty(getRoad1.Road))
                                    {
                                        if (arrKw[1].IndexOf("段") > -1)
                                        {
                                            var sect1 = arrKw[1].Substring(0, arrKw[1].IndexOf("段")) + "段";
                                            getRoad1.Road += sect1;
                                        }
                                        var arrKw1 = crKw.Replace(getRoad1.Road, "");
                                        foreach (var twoWord1 in crossRoadTwoWord)
                                        {
                                            if (arrKw1.IndexOf(twoWord1) > -1)
                                            {
                                                var getRoad2 = SetGISAddress(new SearchGISAddress { Address = arrKw1 });
                                                if (!string.IsNullOrEmpty(getRoad2.Road) && getRoad2.Road.Length > 3 && (getRoad2.Road.EndsWith("街路") || getRoad2.Road.EndsWith("段路") || getRoad2.Road.EndsWith("巷路") || getRoad2.Road.EndsWith("弄路") || getRoad2.Road.EndsWith("道路")))
                                                {
                                                    getRoad2.Road = getRoad2.Road.Remove(getRoad2.Road.Length - 1, 1);
                                                }
                                                if (getRoad2.Road.Length == 2 &&
                                                 (getRoad2.Road.EndsWith("街路") || getRoad2.Road.EndsWith("段路") || getRoad2.Road.EndsWith("巷路") || getRoad2.Road.EndsWith("弄路") || getRoad2.Road.EndsWith("道路")))
                                                {
                                                    getRoad2.Road = "";
                                                    isCrossRoads = true;
                                                    break;
                                                }
                                                // 兩條路不能一樣名字
                                                if (!string.IsNullOrEmpty(getRoad2.Road) && getRoad1.Road != getRoad2.Road)
                                                {
                                                    if (getRoad1.Road.IndexOf(getRoad2.Road) > -1)
                                                    {
                                                        getRoad1.Road = getRoad1.Road.Replace(getRoad2.Road, "");
                                                    }
                                                    if (getRoad2.Road.IndexOf(getRoad1.Road) > -1)
                                                    {
                                                        getRoad2.Road = getRoad2.Road.Replace(getRoad1.Road, "");
                                                    }
                                                    if (!string.IsNullOrEmpty(getRoad1.Road) && !string.IsNullOrEmpty(getRoad2.Road))
                                                    {
                                                        var tempRoad1 = SetGISAddress(new SearchGISAddress { Address = getRoad1.Road });
                                                        if (!string.IsNullOrEmpty(tempRoad1.Road))
                                                        {
                                                            crKw = getRoad1.City + getRoad1.Dist + getRoad1.Dist + getRoad1.Road + "/" + getRoad2.Road + getRoad2.Sect;
                                                        }
                                                        else
                                                        {
                                                            if (getRoad2.Sect.EndsWith("路") || getRoad2.Sect.EndsWith("街") || getRoad2.Sect.EndsWith("道"))
                                                            {
                                                                crKw = getRoad2.City + getRoad2.Dist + getRoad2.Dist + getRoad2.Road + "/" + getRoad2.Sect;
                                                            }
                                                        }
                                                        isCrossRoads = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                        if (isCrossRoads) { break; }
                                    }
                                }
                            }
                        }
                    }
                    #endregion

                    #endregion 判斷是否為交叉路口end

                    if (isCrossRoads && !checkCrossRoads)
                    {
                        // 判斷兩條路是否相同
                        var getCRAddr = SetGISAddress(new SearchGISAddress { Address = crKw });
                        if (!string.IsNullOrEmpty(getCRAddr.Road2) && getCRAddr.Road != getCRAddr.Road2)
                        {
                            // 文字為交叉路口 但設定不判別交叉路口
                            resAddr.Address = getCRAddr.Road + "與" + getCRAddr.Road2 + "交叉路口";
                            return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號, 3).ConfigureAwait(false);
                        }
                        if (string.IsNullOrEmpty(getCRAddr.Road2))
                        {
                            isCrossRoads = false;
                        }
                    }

                    if (checkCrossRoads && isCrossRoads)
                    {
                        #region 處理路名
                        if (crKw.IndexOf("/和美術") > -1) { crKw = crKw.Replace("/和美術", "/美術"); }
                        // 兩條路都有連接字 (上面只判斷一條路有連結字)
                        if (kw.IndexOf("和平東路") > -1 && kw.IndexOf("安和路") > -1) { crKw = "和平東路/安和路"; }
                        if (crKw.IndexOf("段路") > -1) { crKw = crKw.Replace("段路", ""); }
                        #endregion

                        #region 判斷是否有不完整City
                        foreach (var c in allCity)
                        {
                            if (crKw.IndexOf(c) > -1 && c != "新北" && crKw.IndexOf(c + "市") == -1 && crKw.IndexOf(c + "路") == -1 && crKw.IndexOf(c + "街") == -1 && crKw.IndexOf(c + "港路") == -1 && crKw.IndexOf(c + "大道") == -1 && crKw.IndexOf(c + "大學路") == -1)
                            {
                                crKw = crKw.Replace(c, c + "市");
                                break;
                            }
                        }
                        foreach (var c in allCityCounty)
                        {
                            if (crKw.IndexOf(c) > -1 && c != "金門" && crKw.IndexOf(c + "縣") == -1 && crKw.IndexOf(c + "路") == -1 && crKw.IndexOf(c + "街") == -1 && crKw.IndexOf(c + "新村") == -1 && crKw.IndexOf(c + "大道") == -1)
                            {
                                crKw = crKw.Replace(c, c + "縣");
                                break;
                            }
                        }
                        foreach (var c in allCityTwo)
                        {
                            if (crKw.IndexOf(c) > -1 && crKw.IndexOf(c + "市") == -1 && crKw.IndexOf(c + "縣") == -1 && crKw.IndexOf(c + "路") == -1 && crKw.IndexOf(c + "街") == -1 && crKw.IndexOf(c + "大道") == -1)
                            {
                                crKw = crKw.Replace(c, c + "市"); // 選用市
                                break;
                            }
                        }
                        #endregion

                        var getCrossRoadAddr = SetGISAddress(new SearchGISAddress { Address = crKw, IsCrossRoads = true });
                        if (!string.IsNullOrEmpty(getCrossRoadAddr.Road) && string.IsNullOrEmpty(getCrossRoadAddr.Road2) && !string.IsNullOrEmpty(getCrossRoadAddr.Sect2))
                        {
                            // 代表 同路名不同段
                            getCrossRoadAddr.Road2 = getCrossRoadAddr.Road;
                        }
                        if (!string.IsNullOrEmpty(getCrossRoadAddr.Road) && !string.IsNullOrEmpty(getCrossRoadAddr.Road2) &&
                            (getCrossRoadAddr.Road != getCrossRoadAddr.Road2 || getCrossRoadAddr.Road + getCrossRoadAddr.Sect != getCrossRoadAddr.Road2 + getCrossRoadAddr.Sect2))
                        {
                            if (getCrossRoadAddr.Road.Length > 5)
                            {
                                var tempCrossRoadAddr = SetGISAddress(new SearchGISAddress { Address = getCrossRoadAddr.Road, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(tempCrossRoadAddr.Road) && (!string.IsNullOrEmpty(tempCrossRoadAddr.City) || !string.IsNullOrEmpty(tempCrossRoadAddr.Dist)))
                                {
                                    getCrossRoadAddr.Road = tempCrossRoadAddr.Road;
                                }
                            }
                            if (getCrossRoadAddr.Sect2.Length > 5)
                            {
                                var tempCrossRoadAddr = SetGISAddress(new SearchGISAddress { Address = getCrossRoadAddr.Sect2, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(tempCrossRoadAddr.Sect))
                                {
                                    getCrossRoadAddr.Sect2 = tempCrossRoadAddr.Sect;
                                }
                            }
                            resAddr.Address = getCrossRoadAddr.City + getCrossRoadAddr.Dist + getCrossRoadAddr.Road + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect) ? getCrossRoadAddr.Sect + "段" : "") + "與" + getCrossRoadAddr.City2 + getCrossRoadAddr.Dist2 + getCrossRoadAddr.Road2 + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect2) ? getCrossRoadAddr.Sect2 + "段" : "") + "交叉路口";
                            // 只判斷有兩條Road的
                            var getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                            if (getCrossRoad != null)
                            {
                                newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            #region 同縣市有同音不同字的路名
                            if (getCrossRoadAddr.City == "新北市")
                            {
                                if (getCrossRoadAddr.Road == "明義街" || getCrossRoadAddr.Road2 == "明義街")
                                {
                                    if (getCrossRoadAddr.Road == "明義街") { getCrossRoadAddr.Road = "民義街"; }
                                    if (getCrossRoadAddr.Road2 == "明義街") { getCrossRoadAddr.Road2 = "民義街"; }
                                }
                                else
                                {
                                    if (getCrossRoadAddr.Road == "民義街") { getCrossRoadAddr.Road = "明義街"; }
                                    if (getCrossRoadAddr.Road2 == "民義街") { getCrossRoadAddr.Road2 = "明義街"; }
                                }
                                resAddr.Address = getCrossRoadAddr.City + getCrossRoadAddr.Dist + getCrossRoadAddr.Road + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect) ? getCrossRoadAddr.Sect + "段" : "") + "與" + getCrossRoadAddr.City2 + getCrossRoadAddr.Dist2 + getCrossRoadAddr.Road2 + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect2) ? getCrossRoadAddr.Sect2 + "段" : "") + "交叉路口";
                                getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                                if (getCrossRoad != null)
                                {
                                    newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                    newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                    ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                            #endregion

                            // 移除Dist再找一次
                            getCrossRoadAddr.Dist = "";
                            getCrossRoadAddr.Dist2 = "";
                            resAddr.Address = getCrossRoadAddr.City + getCrossRoadAddr.Road + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect) ? getCrossRoadAddr.Sect + "段" : "") + "與" + getCrossRoadAddr.City2 + getCrossRoadAddr.Road2 + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect2) ? getCrossRoadAddr.Sect2 + "段" : "") + "交叉路口";
                            getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                            if (getCrossRoad != null)
                            {
                                newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            // 移除City再找一次
                            getCrossRoadAddr.City = "";
                            getCrossRoadAddr.City2 = "";
                            resAddr.Address = getCrossRoadAddr.Road + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect) ? getCrossRoadAddr.Sect + "段" : "") + "與" + getCrossRoadAddr.Road2 + (!string.IsNullOrEmpty(getCrossRoadAddr.Sect2) ? getCrossRoadAddr.Sect2 + "段" : "") + "交叉路口";
                            getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                            if (getCrossRoad != null)
                            {
                                newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            // 移除第一個字是市的
                            if (crKw.StartsWith("市") && !crKw.StartsWith("市政北") && !crKw.StartsWith("市場南路"))
                            {
                                getCrossRoadAddr = SetGISAddress(new SearchGISAddress { Address = crKw[1..], IsCrossRoads = true });
                                getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                                if (getCrossRoad != null)
                                {
                                    newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                    newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                    ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }

                            // 移除可能只有Dist但沒念"區"
                            if (getCrossRoadAddr.Road.Length > 4)
                            {
                                getCrossRoadAddr.Road = getCrossRoadAddr.Road[2..];
                                getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                                if (getCrossRoad != null)
                                {
                                    newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                    newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                    ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }

                            }
                            if (getCrossRoadAddr.Road2.Length > 4)
                            {
                                getCrossRoadAddr.Road2 = getCrossRoadAddr.Road2[2..];
                                getCrossRoad = await GoASRAPIByCrossRoads(getCrossRoadAddr).ConfigureAwait(false);
                                if (getCrossRoad != null)
                                {
                                    newSpeechAddress.Lng_X = getCrossRoad.Lng_X;
                                    newSpeechAddress.Lat_Y = getCrossRoad.Lat_Y;
                                    ShowAddr(2, getCrossRoad.Addr1, newSpeechAddress, ref resAddr, crossRoad: getCrossRoad.Addr2);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }

                            // 檢查是否滿足交叉路口條件
                            foreach (var str in crossRoadKeyWord)
                            {
                                if (kw.IndexOf(str) > -1 && checkJoin > -1)
                                {
                                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 3).ConfigureAwait(false);
                                }
                            }

                        }
                    }
                }
                #endregion 交叉路口end

                #region 檢查是否有念了地址又有特殊地標
                var tmpOneNum = false;
                checkNum = kw.IndexOf("號");
                if (checkNum == -1 && addOneNum)
                {
                    // 判斷是否有巷口關鍵字
                    foreach (var str in noNumKeyWord)
                    {
                        if (kw.IndexOf(str) > -1)
                        {
                            tmpOneNum = true;
                            break;
                        }
                    }
                }
                // 如果沒念號
                if (checkNum == -1 && !tmpOneNum)
                {
                    string[] arrRoad = { "路", "道", "街" };
                    var getAddrNoNum = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = true });
                    if (!string.IsNullOrEmpty(getAddrNoNum.Road))
                    {
                        foreach (var str in arrRoad)
                        {
                            var idx = getAddrNoNum.Road.IndexOf(str);
                            if (idx > -1 && idx != (getAddrNoNum.Road.Length - 2))
                            {
                                var road = getAddrNoNum.Road.Substring(0, idx) + str;
                                var poi = getAddrNoNum.Road[(idx + 1)..];
                                var kw1 = getAddrNoNum.City + getAddrNoNum.Dist + road + getAddrNoNum.Sect + getAddrNoNum.Lane + getAddrNoNum.Non;
                                if (string.IsNullOrEmpty(poi) && kw.IndexOf("的") > -1 && (kw.IndexOf("的") + 1) != kw.Length)
                                {
                                    poi = kw.Substring(kw.IndexOf("的") + 1, kw.Length - (kw.IndexOf("的") + 2));
                                }
                                if (string.IsNullOrEmpty(poi))
                                {
                                    poi = kw[(kw.IndexOf(getAddrNoNum.Road) + getAddrNoNum.Road.Length)..];
                                }
                                if (!string.IsNullOrEmpty(poi))
                                {
                                    poi = poi.Replace("地下室", "");
                                    var thesaurusNM = "";
                                    var getAddrMK = SetGISAddress(new SearchGISAddress { Address = kw1, IsCrossRoads = false });
                                    if (!string.IsNullOrEmpty(getAddrMK.City)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrMK.City + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddrMK.Dist)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrMK.Dist + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddrMK.Road)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrMK.Road + ") and"; }
                                    thesaurusNM += " FORMSOF(THESAURUS," + poi + ")";
                                    var getMarkName = await GoASRAPI("", thesaurusNM, isNum: false, markName: poi + "-").ConfigureAwait(false);
                                    if (getMarkName != null && !string.IsNullOrEmpty(getMarkName.Memo))
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
                // 如果有號但沒城市
                if (checkNum > -1 && kw.Length > (checkNum + 1))
                {
                    var getAddrNoCity = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = true });
                    var tempAddrNoCity = SetGISAddress(new SearchGISAddress { Address = getAddrNoCity.Num, IsCrossRoads = false });
                    if (string.IsNullOrEmpty(getAddrNoCity.City) && string.IsNullOrEmpty(getAddrNoCity.Dist))
                    {
                        var poi = kw[(checkNum + 1)..];
                        if (poi.Length > 2)
                        {
                            var thesaurusNM = "";
                            if (!string.IsNullOrEmpty(getAddrNoCity.Road)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.Road + ") and"; }
                            if (!string.IsNullOrEmpty(getAddrNoCity.Sect)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.Sect + ") and"; }
                            if (!string.IsNullOrEmpty(getAddrNoCity.Lane)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.Lane + ") and"; }
                            if (!string.IsNullOrEmpty(getAddrNoCity.Non)) { thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.Non + ") and"; }
                            if (!string.IsNullOrEmpty(tempAddrNoCity.Num)) { thesaurusNM += " FORMSOF(THESAURUS," + tempAddrNoCity.Num + ") and"; }
                            thesaurusNM += " FORMSOF(THESAURUS," + poi + ")";
                            var getMarkName = await GoASRAPI("", thesaurusNM, markName: poi).ConfigureAwait(false);
                            if (getMarkName != null && !string.IsNullOrEmpty(getMarkName.Memo))
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(getAddrNoCity.City) && !string.IsNullOrEmpty(getAddrNoCity.Dist) && string.IsNullOrEmpty(getAddrNoCity.Road))
                    {
                        var arrAdd = kw.Split("號");
                        var poi = arrAdd[^1];
                        var tempAddr = getAddrNoCity.City + getAddrNoCity.Dist;
                        var thesaurusNM = "";
                        thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.City + ") and";
                        thesaurusNM += " FORMSOF(THESAURUS," + getAddrNoCity.Dist + ") and";
                        if (!string.IsNullOrEmpty(tempAddrNoCity.Num)) { thesaurusNM += " FORMSOF(THESAURUS," + tempAddrNoCity.Num + ") and"; }
                        if (!string.IsNullOrEmpty(poi))
                        {
                            thesaurusNM += " FORMSOF(THESAURUS," + poi + ") ";
                            var getMarkName = await GoASRAPI("", thesaurusNM, markName: poi).ConfigureAwait(false);
                            if (getMarkName != null && !string.IsNullOrEmpty(getMarkName.Memo))
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                }
                #endregion

                #region 門牌/地標
                var openAddr = kw;

                #region 拆解地址前先檢查
                // 移除巷口等字眼
                if (!addOneNum && !checkCrossRoads)
                {
                    foreach (var str in oneNumReplace)
                    {
                        var arr = str.Split("|");
                        kw = kw.Replace(arr[0], arr[1]);
                    }
                }
                // 判斷是否有巷口等關鍵字
                if (addOneNum && checkNum == -1)
                {
                    foreach (var str in noNumKeyWord)
                    {
                        if (kw.IndexOf(str + "等") == -1 && kw.IndexOf(str) > -1)
                        {
                            kw = kw.Replace(str, str + "等");
                            break;
                        }
                    }
                    foreach (var str in noNumReplace)
                    {
                        var arr = str.Split("|");
                        if (kw.IndexOf(arr[0] + "等") == -1 || kw.IndexOf(arr[0]) > -1)
                        {
                            kw = kw.Replace(arr[0], arr[1]);
                            break;
                        }
                    }
                }

                // 判斷是否有號又有巷口等關鍵字
                var getAddOneNum = false;
                var getAddOneNumWord = "";
                if (checkNum > -1)
                {
                    foreach (var str in noNumKeyWord)
                    {
                        if (kw.IndexOf(str) > -1)
                        {
                            getAddOneNumWord = str.Substring(0, 1);
                            getAddOneNum = true;
                            break;
                        }
                    }
                }

                // 判斷是否有號又有交叉路口等關鍵字
                if (checkNum > -1)
                {
                    var isCrossRoad = false;
                    var idxCrossRoad = 0;
                    foreach (var str in crossRoadKeyWord)
                    {
                        if (kw.IndexOf(str) > -1)
                        {
                            isCrossRoad = true;
                            idxCrossRoad = kw.IndexOf(str) + str.Length;
                            break;
                        }
                    }
                    if (isCrossRoad)
                    {
                        // 直接移除號後面的字
                        kw = kw.Substring(0, kw.IndexOf("號") + 1);
                        #region 若 號在 交叉路口關鍵字後
                        if (checkNum > idxCrossRoad)
                        {
                            var tempAddr = SetGISAddress(new SearchGISAddress { Address = kw[idxCrossRoad..], IsCrossRoads = false });
                            if (!string.IsNullOrEmpty(tempAddr.Sect) && tempAddr.Sect.IndexOf("路上") == -1 && tempAddr.Sect.IndexOf("新街") == -1
                                && (tempAddr.Sect.IndexOf("路") > -1 || tempAddr.Sect.IndexOf("街") > -1 || tempAddr.Sect.IndexOf("道") > -1))
                            {
                                foreach (var s in joinWords) { tempAddr.Sect = tempAddr.Sect.Replace(s, ""); }
                                var _tempAddr = SetGISAddress(new SearchGISAddress { Address = tempAddr.Sect, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_tempAddr.Road)) { tempAddr.Road = _tempAddr.Road; }
                                if (!string.IsNullOrEmpty(_tempAddr.Sect)) { tempAddr.Sect = _tempAddr.Sect; }
                            }
                            var _kw = tempAddr.City + tempAddr.Dist + tempAddr.Road + tempAddr.Sect + tempAddr.Lane + tempAddr.Non + tempAddr.Num;
                            if (!string.IsNullOrEmpty(_kw))
                            {
                                var tempAddrOne = await GoASRAPI(_kw, _kw, isNum: true).ConfigureAwait(false);
                                if (tempAddrOne != null)
                                {
                                    newSpeechAddress.Lng_X = tempAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = tempAddrOne.Lat;
                                    ShowAddr(0, tempAddrOne.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #endregion
                    }
                }
                if (kw.StartsWith("是") || (kw.StartsWith("市") && !kw.StartsWith("市政北") && !kw.StartsWith("市場南路"))) { kw = kw[1..]; }
                if (kw.EndsWith("泡")) { kw = kw.Replace("泡", "號"); }
                if (kw.IndexOf("新店") > -1 && kw.IndexOf("新店區") == -1 && kw.IndexOf("新店路") == -1 && kw.IndexOf("新店北路") == -1 && kw.IndexOf("新店後街") == -1) { kw = kw.Replace("新店", "新店區"); }
                if (kw.IndexOf("新竹市") == -1 && kw.IndexOf("竹市") > -1) { kw = kw.Replace("竹市", "新竹市"); }
                if (kw.IndexOf("新北市") == -1 && kw.IndexOf("新北") > -1 && kw.IndexOf("北路") == -1 && kw.IndexOf("北街") == -1 && kw.IndexOf("新北園路") == -1 && kw.IndexOf("新北大道") == -1)
                {
                    var pattern = @"新北.{1}街"; var regex = new Regex(pattern);   // 不能是 "新北X街"
                    if (!regex.IsMatch(kw)) { kw = kw.Replace("新北", "新北市"); }
                }
                if (kw.IndexOf("台北市") == -1 && kw.IndexOf("台北") > -1) { kw = kw.Replace("台北", "台北市"); }
                if (kw.IndexOf("台北市") == -1 && kw.IndexOf("北市") > -1 && kw.IndexOf("新北市") == -1 && kw.IndexOf("竹北市") == -1) { kw = kw.Replace("北市", "台北市"); }
                if (kw.IndexOf("高雄市") == -1 && kw.IndexOf("高雄") > -1 && kw.IndexOf("高雄大學路") == -1) { kw = kw.Replace("高雄", "高雄市"); }
                if (kw.IndexOf("中和區") == -1 && kw.IndexOf("中和") > -1 && kw.IndexOf("中和街") == -1 && kw.IndexOf("中和路") == -1)
                {
                    var pattern = @"中和.{1}街|中和.{1}路"; var regex = new Regex(pattern);
                    if (!regex.IsMatch(kw)) { kw = kw.Replace("中和", "中和區"); }
                }
                if (kw.IndexOf("台中") > -1 && kw.IndexOf("台中市") == -1 && kw.IndexOf("台中港路") == -1 && kw.IndexOf("台中路") == -1) { kw = kw.Replace("台中", "台中市"); }

                // 移除判斷門牌不需要的贅字
                if (kw.IndexOf("7-ELEVEN") > -1) { kw = kw.Replace("7-ELEVEN", ""); }
                if (kw.IndexOf("7－ELEVEN-") > -1) { kw = kw.Replace("7－ELEVEN-", ""); }
                if (kw.IndexOf("新竹科學園區") > -1 && kw.IndexOf("號") > -1) { kw = kw.Replace("新竹科學園區", ""); }

                #region 特殊判斷
                // 拆解元件會將 1111轉成1，所以這邊要另外處理
                var tempArrLane = kw.Split("111巷");
                if (tempArrLane.Length > 2)
                {
                    var temp1 = tempArrLane[0] + "111巷";
                    var tempLane = kw.Replace(temp1, "");
                    if (tempLane.IndexOf("1111巷") > -1 && temp1.IndexOf("1111巷") == -1)
                    {
                        kw = kw.Replace("1111巷", "").Replace("111巷", "@@@").Replace("@@@", "1111巷");
                    }
                }
                #endregion

                openAddr = kw;

                // 如果號後面有其他字就清掉(如果有兩條路，取後面的)
                var addrRemove = "";
                if (kw.IndexOf("號") > -1 && (kw.IndexOf("號") != (kw.Length - 1)) && kw.Length > 6)
                {
                    var arrKw = kw.Split("號");
                    var _openAddr = "|" + arrKw[^2] + "號"; // 取最後一個號
                    var getAddrNum = SetGISAddress(new SearchGISAddress { Address = _openAddr, IsCrossRoads = false });
                    getAddrNum.Num = getAddrNum.Num.Replace("|", "");
                    var _num = getAddrNum.Num;
                    if (string.IsNullOrEmpty(_num))
                    {
                        #region 把號找回來
                        if (!string.IsNullOrEmpty(getAddrNum.City)) { _openAddr = _openAddr.Replace(getAddrNum.City, ""); }
                        if (!string.IsNullOrEmpty(getAddrNum.Dist)) { _openAddr = _openAddr.Replace(getAddrNum.Dist, ""); }
                        if (!string.IsNullOrEmpty(getAddrNum.Road)) { _openAddr = _openAddr.Replace(getAddrNum.Road, ""); }
                        if (!string.IsNullOrEmpty(getAddrNum.Sect)) { _openAddr = _openAddr.Replace(getAddrNum.Sect, ""); }
                        if (!string.IsNullOrEmpty(getAddrNum.Lane)) { _openAddr = _openAddr.Replace(getAddrNum.Lane, ""); }
                        if (!string.IsNullOrEmpty(getAddrNum.Non)) { _openAddr = _openAddr.Replace(getAddrNum.Non, ""); }
                        var _getAddr = SetGISAddress(new SearchGISAddress { Address = _openAddr, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(_getAddr.Num)) { _num = _getAddr.Num; }
                        #endregion
                    }

                    #region 取另一個號
                    if (!string.IsNullOrEmpty(_num))
                    {
                        var temp = kw.Replace(_num, "");
                        var tempAddrNum = SetGISAddress(new SearchGISAddress { Address = temp, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(tempAddrNum.Num) && tempAddrNum.Num != _num)
                        {
                            addrRemove = tempAddrNum.Num;
                        }
                    }
                    #endregion

                    // 檢查贅字是否有其他道路 (有"到、靠近"就不需要)
                    var oneRoad = "";
                    foreach (var k in arrKw)
                    {
                        if (!string.IsNullOrEmpty(k) && k.IndexOf("到") == -1 && k.IndexOf("靠近") == -1)
                        {
                            var getAddr1 = SetGISAddress(new SearchGISAddress { Address = k, IsCrossRoads = false });
                            var tempAddr1 = SetGISAddress(new SearchGISAddress { Address = openAddr, IsCrossRoads = false });

                            if (string.IsNullOrEmpty(getAddr1.Road))
                            {
                                if (string.IsNullOrEmpty(tempAddr1.City) && !string.IsNullOrEmpty(getAddr1.City))
                                {
                                    tempAddr1.City = getAddr1.City;
                                }
                                if (string.IsNullOrEmpty(tempAddr1.Dist) && !string.IsNullOrEmpty(getAddr1.Dist))
                                {
                                    tempAddr1.Dist = getAddr1.Dist;
                                }
                                openAddr = tempAddr1.City + tempAddr1.Dist + openAddr;
                                break;
                            }
                            // 判斷Road是否有道路基本單位
                            string[] arrRoad = { "路", "道", "街" };
                            var isRoad = false;
                            foreach (var r in arrRoad)
                            {
                                if (k.IndexOf(r) > -1)
                                {
                                    isRoad = true;
                                    break;
                                }
                            }
                            if (!isRoad) { break; }
                            if (string.IsNullOrEmpty(_num) && !string.IsNullOrEmpty(getAddr1.Num)) { _num = getAddr1.Num; }
                            if (!string.IsNullOrEmpty(getAddr1.Road) && getAddr1.Road.Length > 2) // 道路最少2個字
                            {
                                if (string.IsNullOrEmpty(oneRoad))
                                {
                                    oneRoad = getAddr1.Road;
                                    openAddr = getAddr1.City + getAddr1.Dist + getAddr1.Road + getAddr1.Sect + getAddr1.Lane + getAddr1.Non + _num;
                                }
                                else
                                {
                                    openAddr = openAddr.Replace(oneRoad, getAddr1.Road);
                                }
                            }
                            if (string.IsNullOrEmpty(tempAddr1.City) && !string.IsNullOrEmpty(getAddr1.City))
                            {
                                tempAddr1.City = getAddr1.City;
                            }
                            if (string.IsNullOrEmpty(tempAddr1.Dist) && !string.IsNullOrEmpty(getAddr1.Dist))
                            {
                                tempAddr1.Dist = getAddr1.Dist;
                            }
                            if (string.IsNullOrEmpty(tempAddr1.Sect) && !string.IsNullOrEmpty(getAddr1.Sect))
                            {
                                tempAddr1.Sect = getAddr1.Sect;
                            }
                            if (string.IsNullOrEmpty(tempAddr1.Lane) && !string.IsNullOrEmpty(getAddr1.Lane))
                            {
                                tempAddr1.Lane = getAddr1.Lane;
                            }
                            if (string.IsNullOrEmpty(tempAddr1.Non) && !string.IsNullOrEmpty(getAddr1.Non))
                            {
                                tempAddr1.Non = getAddr1.Non;
                            }
                            openAddr = tempAddr1.City + tempAddr1.Dist + tempAddr1.Road + tempAddr1.Sect + tempAddr1.Lane + tempAddr1.Non + _num;
                        }
                    }
                    if (arrKw.Length >= 2)
                    {
                        #region 判斷是否有兩個巷
                        var tempAddrLane = SetGISAddress(new SearchGISAddress { Address = openAddr, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(tempAddrLane.Lane))
                        {
                            var _tempLane = "";
                            var _arrKw = kw.Split(tempAddrLane.Lane);
                            if (_arrKw.Length > 2)
                            {
                                // 代表兩個巷只差一碼
                                var _kw = kw.Replace(_arrKw[0] + tempAddrLane.Lane, "");
                                var _tempAddrLane = SetGISAddress(new SearchGISAddress { Address = _kw, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_tempAddrLane.Lane)) { _tempLane = _tempAddrLane.Lane; }
                            }
                            else
                            {
                                var _kw = kw.Replace(tempAddrLane.Lane, "");
                                var _tempAddrLane = SetGISAddress(new SearchGISAddress { Address = _kw, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_tempAddrLane.Lane)) { _tempLane = _tempAddrLane.Lane; }
                            }
                            // 取後面的
                            if (!string.IsNullOrEmpty(_tempLane)) { openAddr = openAddr.Replace(tempAddrLane.Lane, _tempLane); }
                        }
                        #endregion
                        kw = openAddr; // 清除號後面的字
                    }
                    // 有兩條路
                    var twoAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    if ((!string.IsNullOrEmpty(twoAddr.City) || !string.IsNullOrEmpty(twoAddr.Dist)) && !string.IsNullOrEmpty(twoAddr.Road) && !string.IsNullOrEmpty(oneRoad) && !string.IsNullOrEmpty(twoAddr.Num) && twoAddr.Road != oneRoad)
                    {
                        // 先查後面的Road 沒有再查原先的Road
                        var twoAOnlyAddr = twoAddr.Road + twoAddr.Sect + twoAddr.Lane + twoAddr.Non + twoAddr.Num;
                        if (!string.IsNullOrEmpty(twoAddr.Dist))
                        {
                            twoAOnlyAddr = twoAddr.Dist + twoAOnlyAddr;
                        }
                        var tmpTwoAddr = twoAddr.City + twoAddr.Dist + twoAddr.Road + twoAddr.Sect + twoAddr.Lane + twoAddr.Non + twoAddr.Num;
                        var getTwoAddr = await GoASRAPI(tmpTwoAddr, "", isNum: true, onlyAddr: twoAOnlyAddr).ConfigureAwait(false);
                        if (getTwoAddr != null)
                        {
                            newSpeechAddress.Lng_X = getTwoAddr.Lng;
                            newSpeechAddress.Lat_Y = getTwoAddr.Lat;
                            ShowAddr(0, getTwoAddr.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        getTwoAddr = await GoASRAPI(tmpTwoAddr.Replace(twoAddr.Road, oneRoad), "", isNum: true, onlyAddr: twoAOnlyAddr.Replace(twoAddr.Road, oneRoad)).ConfigureAwait(false);
                        if (getTwoAddr != null)
                        {
                            newSpeechAddress.Lng_X = getTwoAddr.Lng;
                            newSpeechAddress.Lat_Y = getTwoAddr.Lat;
                            ShowAddr(0, getTwoAddr.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                var getAddr = SetGISAddress(new SearchGISAddress { Address = openAddr, IsCrossRoads = false });

                #region 檢查拆解結果
                if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    var tempNum = getAddr.Num.Replace("臨", "").Replace("附", "").Replace("之", "").Replace("號", "");
                    if (tempNum.Any(c => !char.IsDigit(c)))
                    {
                        // 存在非數字的文字
                        var tempAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Num, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(tempAddr.Dist)) { getAddr.Dist = tempAddr.Dist; }
                        if (!string.IsNullOrEmpty(tempAddr.Num)) { getAddr.Num = tempAddr.Num; }
                    }
                }
                if (openAddr.IndexOf("段") > -1 && openAddr.IndexOf("號") > -1 && openAddr.IndexOf("巷") == -1 && openAddr.IndexOf("弄") == -1 && openAddr.IndexOf("之") == -1 && openAddr.IndexOf("段") < openAddr.IndexOf("號"))
                {
                    var getNum = openAddr.Substring(openAddr.IndexOf("段") + 1, openAddr.IndexOf("號") - openAddr.IndexOf("段") - 1);
                    var isNumeric = long.TryParse(getNum, out var _getNum);
                    var newNum = "";
                    if (!isNumeric && getNum.Length > 1 && getAddr.Num.Replace("號", "") != getNum)
                    {
                        foreach (var n in getNum)
                        {
                            foreach (var c in chineseNumList)
                            {
                                if (n.ToString().IndexOf(c.Key) > -1) { newNum += c.Value.ToString(); break; }
                            }
                        }
                        if (!string.IsNullOrEmpty(newNum))
                        {
                            getAddr.Num = newNum + "號";
                            openAddr = openAddr.Replace(getNum, newNum);
                            kw = openAddr;
                        }
                    }
                }
                if (string.IsNullOrEmpty(getAddr.Num) && openAddr.IndexOf("號") > -1)
                {
                    #region 把號找回來
                    var _num = openAddr;
                    if (!string.IsNullOrEmpty(getAddr.City)) { _num = _num.Replace(getAddr.City, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Dist)) { _num = _num.Replace(getAddr.Dist, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Road)) { _num = _num.Replace(getAddr.Road, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Sect)) { _num = _num.Replace(getAddr.Sect, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Lane)) { _num = _num.Replace(getAddr.Lane, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Non)) { _num = _num.Replace(getAddr.Non, ""); }
                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = _num, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(_getAddr.Num)) { getAddr.Num = _getAddr.Num; }
                    #endregion
                }
                if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Address == getAddr.Num)
                {
                    // 拆完只剩下號 這樣不對
                    var _num = getAddr.Num;
                    getAddr = SetGISAddress(new SearchGISAddress { Address = kw.Replace(_num, ""), IsCrossRoads = false });
                    getAddr.Num = _num;
                }
                if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.IndexOf("鄰") > -1)
                {
                    if (getAddr.Num.IndexOf("鄰") < getAddr.Num.IndexOf("號"))
                    {
                        var newNum = getAddr.Num.Substring(getAddr.Num.IndexOf("鄰") + 1);
                        kw = kw.Replace(getAddr.Num, newNum);
                        getAddr.Num = newNum;
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.Num) && string.IsNullOrEmpty(getAddr.Dist) && string.IsNullOrEmpty(getAddr.Road) && openAddr.IndexOf("園區街") == -1 && openAddr.IndexOf("南港") > -1 && openAddr.IndexOf("園區") > -1)
                {
                    var _kw = kw.Replace("南港園區", "").Replace("軟體園區", "").Replace("南港", "");
                    getAddr = SetGISAddress(new SearchGISAddress { Address = _kw, IsCrossRoads = false });
                    getAddr.Dist = "南港區";
                    getAddr.Road = "園區街";
                    kw = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                }
                if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.IndexOf("之") == -1)
                {
                    // 檢查號是否拆解正確
                    foreach (var other in chineseNum)
                    {
                        var arrKw = kw.Split(other);
                        if (arrKw.Length > 1 && !string.IsNullOrEmpty(arrKw[0]))
                        {
                            var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = other, IsCrossRoads = false });
                            kw = kw.Replace(other, getOtherAddr.Num);
                            getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(getAddr.Road))
                    {
                        if (kw.IndexOf("市民大道") > -1 && getAddr.Road == "民大道")
                        {
                            getAddr.Road = "市民大道";
                        }
                        if (kw.IndexOf("縣民大道") > -1 && getAddr.Road == "民大道")
                        {
                            getAddr.Road = "縣民大道";
                        }
                        getAddr.Road = getAddr.Road.Replace("衛生所", "");
                        // 檢查路裡面是否有其他單位
                        var _temp = getAddr.Road;
                        var _getAddr = SetGISAddress(new SearchGISAddress { Address = _temp, IsCrossRoads = false });
                        if (_getAddr.Road.Length > 10)
                        {
                            foreach (var c in allCity)
                            {
                                if (_getAddr.Road.IndexOf(c + "市") > -1)
                                {
                                    if (string.IsNullOrEmpty(getAddr.City))
                                    {
                                        getAddr.City = c + "市";
                                    }
                                    var arrR = _getAddr.Road.Split(c + "市");
                                    if (arrR.Length > 1)
                                    {
                                        _temp = arrR[1];
                                        if (!string.IsNullOrEmpty(_temp))
                                        {
                                            _getAddr = SetGISAddress(new SearchGISAddress { Address = _temp, IsCrossRoads = false });
                                        }
                                    }
                                    break;
                                }
                            }
                            foreach (var c in allCityCounty)
                            {
                                if (_getAddr.Road.IndexOf(c + "縣") > -1)
                                {
                                    if (string.IsNullOrEmpty(getAddr.City))
                                    {
                                        getAddr.City = c + "市";
                                    }
                                    var arrR = _getAddr.Road.Split(c + "市");
                                    if (arrR.Length > 1)
                                    {
                                        _temp = arrR[1];
                                        if (!string.IsNullOrEmpty(_temp))
                                        {
                                            _getAddr = SetGISAddress(new SearchGISAddress { Address = _temp, IsCrossRoads = false });
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(_getAddr.Road))
                        {
                            _temp = _temp.Replace(_getAddr.Road, "");
                            getAddr.Road = _getAddr.Road;
                        }
                        if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(_getAddr.Dist))
                        {
                            _temp = _temp.Replace(_getAddr.Dist, "");
                            getAddr.Dist = _getAddr.Dist;
                        }
                        if (string.IsNullOrEmpty(getAddr.Dist) && string.IsNullOrEmpty(_getAddr.Dist) && getAddr.Road.IndexOf("區") > 0 && !distRoad.Any(x => getAddr.Road.IndexOf(x) > -1))
                        {
                            var newDist = getAddr.Road.Substring(0, getAddr.Road.IndexOf("區") + 1);
                            if (newDist.Length > 4) { getAddr.Road = getAddr.Road.Replace(newDist, ""); }
                        }
                        if (string.IsNullOrEmpty(getAddr.Sect) && !string.IsNullOrEmpty(_getAddr.Sect))
                        {
                            _temp = _temp.Replace(_getAddr.Sect, "");
                            getAddr.Sect = _getAddr.Sect;
                        }
                        if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(_getAddr.Lane))
                        {
                            _temp = _temp.Replace(_getAddr.Lane, "");
                            getAddr.Lane = _getAddr.Lane;
                        }
                    }
                    if (getAddr.Num.IndexOf("對") > -1)
                    {
                        var idxR = getAddr.Num.IndexOf("對");
                        getAddr.Num = getAddr.Num.Substring(idxR + 1, getAddr.Num.Length - idxR - 1);
                        var temp = Regex.Replace(getAddr.Num, @"\d", "");
                        foreach (var c in temp)
                        {
                            var _c = c.ToString();
                            if (_c != "臨" || _c != "附" || _c != "之")
                            {
                                getAddr.Num = getAddr.Num.Replace(_c, "");
                            }
                        }
                    }
                    if (getAddr.Num.IndexOf("鄰") > -1)
                    {
                        var arrNum = getAddr.Num.Split("鄰");
                        getAddr.Num = arrNum[^1];
                    }
                }

                // 檢查巷裡面是否有號
                if (!string.IsNullOrEmpty(getAddr.Lane) && string.IsNullOrEmpty(getAddr.Num) && getAddr.Lane.IndexOf("號") > 1)
                {
                    var idxNum = getAddr.Lane.IndexOf("號");
                    var idxLane = getAddr.Lane.IndexOf("巷");
                    if (idxNum < idxLane)
                    {
                        var tempLane = getAddr.Lane.Substring(idxNum + 1, getAddr.Lane.Length - idxNum - 1);
                        getAddr.Num = getAddr.Lane.Replace(tempLane, "");
                        getAddr.Lane = tempLane;
                    }
                }

                // 檢查段
                if (!string.IsNullOrEmpty(getAddr.Sect))
                {
                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Sect, IsCrossRoads = false });
                    if (string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(_getAddr.City))
                    {
                        getAddr.City = _getAddr.City;
                        getAddr.Sect = getAddr.Sect.Replace(getAddr.City, "");
                    }
                    if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(_getAddr.Dist))
                    {
                        getAddr.Dist = _getAddr.Dist;
                        getAddr.Sect = getAddr.Sect.Replace(getAddr.Dist, "");
                    }
                }

                if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    // 移除不可能存在的字
                    getAddr.Road = getAddr.Road.Replace("戶政事務所", "");
                    // 把工業區加回來
                    if (kw.IndexOf("工業區") > -1 && getAddr.Road.IndexOf("工業區") == -1)
                    {
                        getAddr.Road = "工業區" + getAddr.Road;
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.Road) && getAddr.City?.IndexOf("新竹") > -1 && getAddr.Road.IndexOf("科學園區") > -1)
                {
                    getAddr.Road = getAddr.Road.Replace("科學園區", "");
                }

                if (!string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.Length > 10)
                {
                    // 路名太長不正常
                    var _r = getAddr.Road.IndexOf("區");
                    if (_r > 2)
                    {
                        var _getAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Road[(_r - 2)..], IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(_getAddr.Dist)) { getAddr.Dist = _getAddr.Dist; }
                        if (!string.IsNullOrEmpty(_getAddr.Road)) { getAddr.Road = _getAddr.Road; }
                    }
                }

                // 檢查是否門牌單位反著念的
                if (!string.IsNullOrEmpty(addrRemove))
                {
                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = addrRemove, IsCrossRoads = false });
                    if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(_getAddr.Dist)) { getAddr.Dist = _getAddr.Dist; }
                    if (string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(_getAddr.Road)) { getAddr.Road = _getAddr.Road; }
                    if (string.IsNullOrEmpty(getAddr.Sect) && !string.IsNullOrEmpty(_getAddr.Sect)) { getAddr.Sect = _getAddr.Sect; }
                    if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(_getAddr.Lane)) { getAddr.Lane = _getAddr.Lane; }
                    if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(_getAddr.Non)) { getAddr.Non = _getAddr.Non; }
                    if (string.IsNullOrEmpty(_getAddr.Num) && addrRemove.IndexOf("號") > -1) { _getAddr.Num = addrRemove; }
                    if (!string.IsNullOrEmpty(_getAddr.Num) && !string.IsNullOrEmpty(getAddr.Num) && _getAddr.Num != getAddr.Num && !string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        // 代表有兩個門牌號 先完整查詢一次
                        var tempComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non;
                        var tempAddrOne = await GoASRAPI(tempComb + getAddr.Num, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                        if (tempAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = tempAddrOne.Lng;
                            newSpeechAddress.Lat_Y = tempAddrOne.Lat;
                            ShowAddr(0, tempAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        tempAddrOne = await GoASRAPI(tempComb + _getAddr.Num, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + _getAddr.Num).ConfigureAwait(false);
                        if (tempAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = tempAddrOne.Lng;
                            newSpeechAddress.Lat_Y = tempAddrOne.Lat;
                            ShowAddr(0, tempAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 同縣市同時存在相似音字路名
                if (kw.IndexOf("平鎮") > -1 && (kw.IndexOf("興隆路") > -1 || kw.IndexOf("新榮路") > -1))
                {
                    if (getAddr.Road == "興隆路") { getAddr.Road2 = "新榮路"; }
                    if (getAddr.Road == "新榮路") { getAddr.Road2 = "興隆路"; }
                }
                if (kw.IndexOf("楊梅") > -1 && (kw.IndexOf("新農街") > -1 || kw.IndexOf("興隆街") > -1))
                {
                    if (getAddr.Road == "新農街") { getAddr.Road2 = "興隆街"; }
                    if (getAddr.Road == "興隆街") { getAddr.Road2 = "新農街"; }
                }
                #endregion

                #region 路名存在兩個道路單位
                if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    foreach (var r in roadTwoName)
                    {
                        if (getAddr.Road == r)
                        {
                            var trAddr = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            var getTrAddr = await GoASRAPI(trAddr, trAddr, isNum: true, onlyAddr: trAddr).ConfigureAwait(false);
                            if (getTrAddr != null)
                            {
                                newSpeechAddress.Lng_X = getTrAddr.Lng;
                                newSpeechAddress.Lat_Y = getTrAddr.Lat;
                                ShowAddr(0, getTrAddr.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            break;
                        }
                    }
                }
                #endregion

                // 重組地址
                var addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;

                if (!string.IsNullOrEmpty(getAddr.Num))
                {
                    resAddr.Address = addrComb;
                    if (getAddr.Num.IndexOf("好號") > -1) { getAddr.Num = getAddr.Num.Replace("好號", "號"); }
                }
                else
                {
                    resAddr.Address = kw;
                }
                if ((string.IsNullOrEmpty(getAddr.City) || string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Road))
                {
                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Road, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(_getAddr.City)) { getAddr.City = _getAddr.City; }
                    if (!string.IsNullOrEmpty(_getAddr.Dist)) { getAddr.Dist = _getAddr.Dist; }
                    if (!string.IsNullOrEmpty(_getAddr.Road)) { getAddr.Road = _getAddr.Road; }
                }
                if (!string.IsNullOrEmpty(getAddr.Dist) && getAddr.Dist.StartsWith("市"))
                {
                    getAddr.Dist = getAddr.Dist[1..];
                }
                if (!string.IsNullOrEmpty(getAddr.City) && getAddr.City == getAddr.Dist)
                {
                    // 如果重複City就移除後再重組一次
                    getAddr.Dist = "";
                    addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    getAddr = SetGISAddress(new SearchGISAddress { Address = addrComb, IsCrossRoads = false });
                    resAddr.Address = addrComb;
                    addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    kw = kw.Replace(getAddr.City, "").Insert(kw.IndexOf(getAddr.City), getAddr.City);
                }
                if (string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist))
                {
                    // 補城市
                    getAddr.City = await GetCityByDist(new DistRequest { Dist = getAddr.Dist }).ConfigureAwait(false);
                    kw = kw.Replace(getAddr.Dist, getAddr.City + getAddr.Dist);
                }
                if (!string.IsNullOrEmpty(getAddr.Road))
                {
                    if (getAddr.Road.EndsWith("街路") && getAddr.Road != "大街路" && getAddr.Road != "中街路" && getAddr.Road != "新街路" && getAddr.Road != "舊街路")
                    {
                        getAddr.Road = getAddr.Road.Replace("街路", "街");
                    }
                    if (getAddr.Road.EndsWith("道路") && !roadTwoName.Contains(getAddr.Road))
                    {
                        getAddr.Road = getAddr.Road.Replace("道路", "道");
                    }
                    if (getAddr.Road.IndexOf("在") > -1)
                    {
                        var _road = getAddr.Road.Replace("在", "");
                        kw = kw.Replace(getAddr.Road, _road);
                        getAddr.Road = _road;
                    }
                    if (getAddr.Road.IndexOf("安坑") > -1 && getAddr.Road.IndexOf("安坑路") == -1)
                    {
                        getAddr.Road = getAddr.Road.Replace("安坑", "");
                    }
                    if (getAddr.Road.IndexOf("崇德十二路") > -1 && string.IsNullOrEmpty(getAddr.Dist))
                    {
                        getAddr.Dist = "北屯區";
                    }
                }

                if (string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Lane) && kw.IndexOf("號") > -1)
                {
                    var arrKw = kw.Split("巷");
                    // 如果有號又有兩個巷取後面 可能導致SetGISAddress拆解錯誤
                    if (arrKw.Length > 2 && !string.IsNullOrEmpty(arrKw[1]))
                    {
                        if (getAddr.Lane.EndsWith("街巷"))
                        {
                            getAddr.Road = getAddr.Lane.Replace("街巷", "街");
                        }
                        if (getAddr.Lane.EndsWith("路巷"))
                        {
                            getAddr.Road = getAddr.Lane.Replace("路巷", "路");
                        }
                        getAddr.Lane = arrKw[1] + "巷";
                        if (!string.IsNullOrEmpty(getAddr.Non))
                        {
                            getAddr.Non = getAddr.Non.Replace(arrKw[1], "");
                        }
                    }
                }
                if (getAddr.Sect == "段") { getAddr.Sect = ""; }
                if (getAddr.Lane == "巷") { getAddr.Lane = ""; }
                if (getAddr.Non == "弄") { getAddr.Non = ""; }

                #region 如果是舊單位要換
                if (getAddr.City == "台北縣" || getAddr.City == "新北市")
                {
                    getAddr.City = "新北市";
                    kw = kw.Replace("台北縣", "新北市");
                    if (!string.IsNullOrEmpty(getAddr.Dist) && getAddr.Dist.IndexOf("區") == -1)
                    {
                        var newDist = getAddr.Dist.Substring(0, 2) + "區";
                        kw = kw.Replace(getAddr.Dist, newDist);
                        getAddr.Dist = newDist;
                    }
                    addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                }
                if (getAddr.City == "桃園縣" || getAddr.City == "桃園市")
                {
                    if (!string.IsNullOrEmpty(getAddr.Dist) && getAddr.Dist.IndexOf("區") == -1)
                    {
                        var newDist = getAddr.Dist.Substring(0, 2) + "區";
                        kw = kw.Replace(getAddr.Dist, newDist);
                        getAddr.Dist = newDist;
                        addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    }
                }
                #endregion

                #region 判斷拆解資料
                if (!string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Road) && (!string.IsNullOrEmpty(getAddr.Sect) || !string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getAddr.Non)))
                {
                    kw = addrComb;
                }
                if (!string.IsNullOrEmpty(addrComb) && kw.EndsWith("燈"))
                {
                    kw = kw.Replace("燈", "");
                }
                if (!string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.Sect))
                {
                    if (getAddr.Sect.Length > 5)
                    {
                        // 段最長5個字 過長不正常
                        var newAddr = getAddr.Sect;
                        if (newAddr.IndexOf("巷") > -1)
                        {
                            var arr = newAddr.Split("巷");
                            newAddr = arr[1];
                        }
                        var newGetAddr = SetGISAddress(new SearchGISAddress { Address = newAddr, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(newGetAddr.Sect)) { getAddr.Sect = newGetAddr.Sect; }
                    }
                    var arrSect = getAddr.Sect.Split("段");
                    if (arrSect.Length > 0 && !string.IsNullOrEmpty(arrSect[1]))
                    {
                        getAddr.Sect = arrSect[1] + "段";
                    }
                }
                if (string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.Sect))
                {
                    if (getAddr.Sect.Length > 5)
                    {
                        var newGetAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Sect, IsCrossRoads = false });
                        if (string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(newGetAddr.City)) { getAddr.City = newGetAddr.City; }
                        if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(newGetAddr.Dist)) { getAddr.Dist = newGetAddr.Dist; }
                        if (!string.IsNullOrEmpty(newGetAddr.Road) && getAddr.Road != newGetAddr.Road) { getAddr.Road = newGetAddr.Road; }
                        if (!string.IsNullOrEmpty(newGetAddr.Sect)) { getAddr.Sect = newGetAddr.Sect; }
                        if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(newGetAddr.Lane)) { getAddr.Lane = newGetAddr.Lane; }
                        if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(newGetAddr.Non)) { getAddr.Non = newGetAddr.Non; }
                        if (!string.IsNullOrEmpty(newGetAddr.Num)) { getAddr.Num = newGetAddr.Num; }
                        if ((!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Sect) && (!string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getAddr.Non)))
                        {
                            kw = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        }
                    }
                }
                if (!addOneNum && string.IsNullOrEmpty(getAddr.Num))
                {
                    // 判斷是否有巷口或路口等字眼，有就同遠傳一樣，加1號
                    foreach (var str in oneNumReplace)
                    {
                        var arrM = str.Split("|");
                        if (kw.IndexOf(arrM[0]) > -1)
                        {
                            getAddr.Num = "1號";
                            break;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.Lane) && string.IsNullOrEmpty(getAddr.Non) && getAddr.Lane.IndexOf("弄") > -1)
                {
                    // 檢查是否Non被放到Lane裡面了
                    var oldLane = getAddr.Lane;
                    var idxNon = getAddr.Lane.IndexOf("弄");
                    getAddr.Non = getAddr.Lane.Substring(0, idxNon + 1);
                    if (getAddr.Non == "弄") { getAddr.Non = ""; }
                    getAddr.Lane = getAddr.Lane[(idxNon + 1)..];
                    kw = kw.Replace(oldLane, getAddr.Lane + getAddr.Non);
                }
                if (string.IsNullOrEmpty(getAddr.Num) && kw.IndexOf("號") > -1)
                {
                    var _kw = kw;
                    if (!string.IsNullOrEmpty(addrComb))
                    {
                        _kw = kw.Replace(addrComb, "");
                    }
                    var newGetAddr = SetGISAddress(new SearchGISAddress { Address = _kw, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(newGetAddr.Num))
                    {
                        getAddr.Num = newGetAddr.Num;
                    }
                }
                if (!checkMarkName && !addOneNum && string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                {
                    // 如果有其他道路單位但沒有給號 就幫帶1號去查
                    getAddr.Num = "1號";
                }
                if (!checkMarkName && !addOneNum && string.IsNullOrEmpty(getAddr.Num))
                {
                    // 為了擋特殊地標不給查用
                    resAddr.Address = addrComb;
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號, 2).ConfigureAwait(false);
                }
                if (!checkMarkName && string.IsNullOrEmpty(getAddr.Num) &&
                    ((kw.IndexOf("捷運") > -1 && kw.IndexOf("捷運路") == -1) || kw.IndexOf("車站") > -1))
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 2).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(getAddr.Num))
                {
                    // 檢查是否有重複唸Non且被放到Num
                    var getNon = getAddr.Num.IndexOf("弄");
                    if (getNon > -1)
                    {
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Num, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getNewAddr.Non))
                        {
                            getAddr.Non2 = getAddr.Non;
                            getAddr.Non = getNewAddr.Non; // 用後面的
                        }
                        getAddr.Num = getAddr.Num[(getNon + 1)..];
                        addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.Lane))
                {
                    // 檢查是否為中文數字
                    foreach (var c in chineseNumList)
                    {
                        if (getAddr.Lane == c.Key + "巷")
                        {
                            getAddr.Lane = c.Value + "巷";
                            break;
                        }
                        else if (getAddr.Lane.EndsWith(c.Key + "巷"))
                        {
                            var _lane = getAddr.Lane.Replace(c.Key + "巷", "");
                            if (reg1.IsMatch(_lane))
                            {
                                getAddr.Lane = _lane + c.Value + "巷";
                                if (_lane.EndsWith("0"))
                                {
                                    getAddr.Lane2 = _lane.TrimEnd('0') + c.Value + "巷";
                                }
                                break;
                            }
                        }
                    }
                }
                #endregion

                #region 如果有要判斷特殊地標且沒有號時，需要自己先找一次
                if (checkMarkName && kw.IndexOf("號") == -1)
                {
                    if (!string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) &&
                           (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        // 可能最後一個字是數字 所以先拿完整地址找一次
                        var getAddrKw = await GoASRAPI(getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num, "", isNum: true).ConfigureAwait(false);
                        resAddr.Address = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        if (getAddrKw != null)
                        {
                            newSpeechAddress.Lng_X = getAddrKw.Lng;
                            newSpeechAddress.Lat_Y = getAddrKw.Lat;
                            ShowAddr(0, getAddrKw.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    var getMarkName = await GoASRAPI(kw, "", markName: kw).ConfigureAwait(false);
                    resAddr.Address = kw;
                    if (getMarkName != null)
                    {
                        newSpeechAddress.Lng_X = getMarkName.Lng;
                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    if (kw.IndexOf("醫院") > -1 && kw.IndexOf("急診室") > -1)
                    {
                        var _kwMarkName = kw.Remove(kw.IndexOf("急診室"));
                        if (!string.IsNullOrEmpty(_kwMarkName))
                        {
                            getMarkName = await GoASRAPI(_kwMarkName, "", markName: _kwMarkName).ConfigureAwait(false);
                            resAddr.Address = _kwMarkName;
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    if (kw.IndexOf("車站") > -1 && kw.IndexOf("火車站") == -1 && kw.IndexOf("新市") == -1)
                    {
                        var _kwMarkName = kw.Replace("車站", "火車站");
                        getMarkName = await GoASRAPI(_kwMarkName, "", markName: _kwMarkName).ConfigureAwait(false);
                        resAddr.Address = _kwMarkName;
                        if (getMarkName != null)
                        {
                            newSpeechAddress.Lng_X = getMarkName.Lng;
                            newSpeechAddress.Lat_Y = getMarkName.Lat;
                            ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    if ((string.IsNullOrEmpty(addrComb) || addrComb == getAddr.Road) && !string.IsNullOrEmpty(getAddr.Road) && kw.Length > 6 && getAddr.Road.Length <= 10)
                    {
                        #region 可能有路名+地標
                        if (getAddr.Road.Length != kw.Length)
                        {
                            var kwR = getAddr.Road;
                            var kwM = kw.Replace(kwR, "");
                            var tempThesaurusR = "";
                            var tempThesaurusM = "";
                            for (var i = 0; i < kwR.Length; i++)
                            {
                                if (i <= (kwR.Length - 5))
                                {
                                    tempThesaurusR += " FORMSOF(THESAURUS," + kwR.Substring(i, 5) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            for (var i = 0; i < kwR.Length; i++)
                            {
                                if (i <= (kwR.Length - 4))
                                {
                                    tempThesaurusR += " FORMSOF(THESAURUS," + kwR.Substring(i, 4) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            for (var i = 0; i < kwR.Length; i++)
                            {
                                if (i <= (kwR.Length - 3))
                                {
                                    tempThesaurusR += " FORMSOF(THESAURUS," + kwR.Substring(i, 3) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }

                            for (var i = 0; i < kwM.Length; i++)
                            {
                                if (i <= (kwM.Length - 5))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + kwM.Substring(i, 5) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            for (var i = 0; i < kwM.Length; i++)
                            {
                                if (i <= (kwM.Length - 4))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + kwM.Substring(i, 4) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }
                            for (var i = 0; i < kwM.Length; i++)
                            {
                                if (i <= (kwM.Length - 3))
                                {
                                    tempThesaurusM += " FORMSOF(THESAURUS," + kwM.Substring(i, 3) + ") or";
                                }
                                else
                                {
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(tempThesaurusR) && !string.IsNullOrEmpty(tempThesaurusM))
                            {
                                tempThesaurusR = tempThesaurusR.Remove(tempThesaurusR.Length - 2, 2).Trim();
                                tempThesaurusM = tempThesaurusM.Remove(tempThesaurusM.Length - 2, 2).Trim();
                                var tempMarkName = "";
                                if (tempThesaurusM.IndexOf("or") == -1) { tempMarkName = kwM; }
                                getMarkName = await GoASRAPI("", "(" + tempThesaurusR + ") and (" + tempThesaurusM + ")", isNum: false, markName: tempMarkName).ConfigureAwait(false);
                                if (getMarkName != null)
                                {
                                    if (!string.IsNullOrEmpty(tempMarkName) && !string.IsNullOrEmpty(getMarkName.Memo) && getMarkName.Memo != tempMarkName)
                                    {
                                        getMarkName.Memo = "";
                                    }
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #endregion

                        // 純地標
                        var tempThesaurus = "";
                        for (var i = 0; i < kw.Length; i++)
                        {
                            if (i <= (kw.Length - 6))
                            {
                                tempThesaurus += " FORMSOF(THESAURUS," + kw.Substring(i, 6) + ") or";
                            }
                            else
                            {
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(tempThesaurus))
                        {
                            tempThesaurus = tempThesaurus.Remove(tempThesaurus.Length - 2, 2).Trim();
                            getMarkName = await GoASRAPI("", tempThesaurus, isNum: false).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                if (getMarkName.Memo?.IndexOf("-") > -1)
                                {
                                    var arrM = getMarkName.Memo.Split("-");
                                    if (!string.IsNullOrEmpty(arrM[1]) && kw.IndexOf(arrM[1]) > -1)
                                    {
                                        newSpeechAddress.Lng_X = getMarkName.Lng;
                                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    newSpeechAddress.Lng_X = getMarkName.Lng;
                                    newSpeechAddress.Lat_Y = getMarkName.Lat;
                                    ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        tempThesaurus = "";
                        for (var i = 0; i < kw.Length; i++)
                        {
                            if (i <= (kw.Length - 5))
                            {
                                tempThesaurus += " FORMSOF(THESAURUS," + kw.Substring(i, 5) + ") or";
                            }
                            else
                            {
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(tempThesaurus))
                        {
                            tempThesaurus = tempThesaurus.Remove(tempThesaurus.Length - 2, 2).Trim();
                            getMarkName = await GoASRAPI("", tempThesaurus, isNum: false).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        tempThesaurus = "";
                        for (var i = 0; i < kw.Length; i++)
                        {
                            if (i <= (kw.Length - 4))
                            {
                                tempThesaurus += " FORMSOF(THESAURUS," + kw.Substring(i, 4) + ") or";
                            }
                            else
                            {
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(tempThesaurus))
                        {
                            tempThesaurus = tempThesaurus.Remove(tempThesaurus.Length - 2, 2).Trim();
                            getMarkName = await GoASRAPI("", tempThesaurus, isNum: false).ConfigureAwait(false);
                            if (getMarkName != null)
                            {
                                newSpeechAddress.Lng_X = getMarkName.Lng;
                                newSpeechAddress.Lat_Y = getMarkName.Lat;
                                ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                }
                #endregion

                #region 判斷是否沒有贅字且地址有缺
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road))
                {
                    var allCitys = allCity.Concat(allCityCounty).Concat(allCityTwo).ToArray();
                    foreach (var c in allCitys)
                    {
                        if (getAddr.Road.IndexOf(c) > -1)
                        {
                            if (string.IsNullOrEmpty(getAddr.Road.Replace(c, "")))
                            {
                                return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題, 1).ConfigureAwait(false);
                            }
                        }
                    }
                    #region 檢查City+Dist是否都卡在Road裡了
                    #region 先判斷路名是否有區
                    if (!distRoad.Any(x => getAddr.Road.IndexOf(x) > -1))
                    {
                        // 有區且前後都有路就取後面的
                        var tempRoad = getAddr.Road;
                        var isCrossRoads = false;
                        foreach (var twoWord in crossRoadTwoWord)
                        {
                            var arrKw = tempRoad.Split(twoWord);
                            if (arrKw.Length >= 2)
                            {
                                var getRoad1 = SetGISAddress(new SearchGISAddress { Address = arrKw[0] + twoWord });
                                if (!string.IsNullOrEmpty(getRoad1.Road))
                                {
                                    var arrKw1 = tempRoad.Replace(getRoad1.Road, "");
                                    if (!string.IsNullOrEmpty(arrKw1))
                                    {
                                        foreach (var twoWord1 in crossRoadTwoWord)
                                        {
                                            if (arrKw1.IndexOf(twoWord1) > -1)
                                            {
                                                var getRoad2 = SetGISAddress(new SearchGISAddress { Address = arrKw1 });
                                                if (!string.IsNullOrEmpty(getRoad2.Road) && getRoad1.Road != getRoad2.Road)
                                                {
                                                    tempRoad = tempRoad.Replace(getRoad1.Road, "");
                                                    isCrossRoads = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    if (isCrossRoads) { break; }
                                }
                            }
                        }
                        if (getAddr.Road != tempRoad) { getAddr.Road = tempRoad; }
                    }
                    #endregion
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Road, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getNewAddr.City))
                    {
                        getAddr.City = getNewAddr.City;
                    }
                    if (!string.IsNullOrEmpty(getNewAddr.Dist))
                    {
                        getAddr.Dist = getNewAddr.Dist;
                    }
                    if (!string.IsNullOrEmpty(getNewAddr.Road))
                    {
                        // 檢查路裡面是否有City 這邊先不刪同時存在"市"、"縣"單位的City
                        getAddr.Road = getNewAddr.Road;
                        foreach (var c in allCity.Concat(allCityCounty))
                        {
                            if (getAddr.Road == (c + "路") || getAddr.Road == (c + "街") || getAddr.Road == (c + "大道"))
                            {
                                break;
                            }
                            if (getAddr.Road.IndexOf(c + "市") > -1)
                            {
                                getAddr.Road = getAddr.Road.Replace(c + "市", "");
                                break;
                            }
                            if (getAddr.Road.IndexOf(c + "縣") > -1)
                            {
                                getAddr.Road = getAddr.Road.Replace(c + "縣", "");
                                break;
                            }
                            if (getAddr.Road.IndexOf(c) > -1 && getAddr.Road.IndexOf("雲林新村") == -1)
                            {
                                getAddr.Road = getAddr.Road.Replace(c, "");
                                break;
                            }
                        }
                    }
                    #endregion
                }

                var addrRemoveOne = kw;
                if (!string.IsNullOrEmpty(getAddr.City)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.City, ""); }
                if (!string.IsNullOrEmpty(getAddr.Dist)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Dist, ""); }
                if (!string.IsNullOrEmpty(getAddr.Road)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Road, ""); }
                if (!string.IsNullOrEmpty(getAddr.Sect)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Sect, ""); }
                if (!string.IsNullOrEmpty(getAddr.Lane)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Lane, ""); }
                if (!string.IsNullOrEmpty(getAddr.Non)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Non, ""); }
                if (!string.IsNullOrEmpty(getAddr.Num)) { addrRemoveOne = addrRemoveOne.Replace(getAddr.Num, ""); }
                if (addrRemoveOne.Length == 0)
                {
                    if (string.IsNullOrEmpty(getAddr.Num) && !addOneNum)
                    {
                        // 沒有贅字且沒有號
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(getAddr.Num) && string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist)
                        && string.IsNullOrEmpty(getAddr.Road) && string.IsNullOrEmpty(getAddr.Lane))
                    {
                        // 沒有贅字且只有號
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題, 1).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) &&
                                                 string.IsNullOrEmpty(getAddr.Road) && string.IsNullOrEmpty(getAddr.Lane))
                    {
                        // 有前有後 但缺中間
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題, 1).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 檢查是否為數字道路
                    if (!string.IsNullOrEmpty(getAddr.Road))
                    {
                        var numToStr = AddressSpilt.NumToTaiwanese(addrRemoveOne);
                        if (numToStr == getAddr.Road)
                        {
                            addrRemoveOne = "";
                        }
                    }
                    // 判斷贅字裡是否有City
                    if (string.IsNullOrEmpty(getAddr.City))
                    {
                        foreach (var c in allCity.Concat(allCityCounty).Concat(allCityTwo))
                        {
                            if (getAddr.Road == (c + "路") || getAddr.Road == (c + "街"))
                            {
                                break;
                            }
                            if (addrRemoveOne.IndexOf(c + "市") > -1)
                            {
                                getAddr.City = c + "市";
                                addrRemoveOne.Replace(c + "市", "");
                                break;
                            }
                            if (addrRemoveOne.IndexOf(c + "縣") > -1)
                            {
                                getAddr.City = c + "縣";
                                addrRemoveOne.Replace(c + "縣", "");
                                break;
                            }
                        }
                    }
                    // 判斷贅字裡是否有Road
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = addrRemoveOne, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getNewAddr.Road))
                    {
                        getAddr.Road2 = getNewAddr.Road;
                    }
                }
                // 判斷號裡面是否有贅字
                if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.Length > 8)
                {
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddr.Num, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getNewAddr.Num))
                    {
                        getAddr.Num = getNewAddr.Num;
                    }
                }
                // 檢查路是不是重複
                if (!string.IsNullOrEmpty(getAddr.Road))
                {
                    var theRoad1 = getAddr.Road.Substring(0, getAddr.Road.Length / 2);
                    var theRoad2 = getAddr.Road[(getAddr.Road.Length / 2)..];
                    if (theRoad1 == theRoad2)
                    {
                        getAddr.Road = theRoad2;
                    }
                    // 是否有兩條路
                    if (!string.IsNullOrEmpty(getAddr.Num))
                    {
                        // 兩條路單位一樣
                        foreach (var twoWord in crossRoadTwoWord)
                        {
                            var temp = getAddr.Road.Replace(twoWord, "");
                            if (getAddr.Road.Length - temp.Length == 2)
                            {
                                var arrKw = getAddr.Road.Split(twoWord);
                                if (arrKw.Length >= 2 && !string.IsNullOrEmpty(arrKw[1]))
                                {
                                    getAddr.Road = arrKw[1] + twoWord;
                                    break;
                                }
                            }
                        }
                        // 有說修改地址關鍵字
                        string[] upWord = { "不對是", "不對市", "錯了", "改成" };
                        foreach (var word in upWord)
                        {
                            var arrKw = getAddr.Road.Split(word);
                            if (arrKw.Length >= 2 && arrKw[1] != "")
                            {
                                getAddr.Road = arrKw[1];
                                break;
                            }
                        }
                    }
                }
                // 如果沒有地址 且 沒有贅字或贅字為全英文或數字
                if (string.IsNullOrEmpty(addrComb) && (string.IsNullOrEmpty(addrRemoveOne) || reg1.IsMatch(addrRemoveOne)))
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題, 2).ConfigureAwait(false);
                }
                var noCityDist = false;
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist))
                {
                    noCityDist = true;
                }

                // 檢查是否為捷運
                if (!string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.IndexOf("捷運") > -1 && getAddr.Road.IndexOf("捷運路") == -1 && getAddr.Road.IndexOf("捷運站") == -1 && getAddr.Road.IndexOf("站") == -1)
                {
                    getAddr.Road += "站";
                    kw = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    var getMarkName = await GoASRAPI(kw, "", markName: kw).ConfigureAwait(false);
                    if (getMarkName != null)
                    {
                        newSpeechAddress.Lng_X = getMarkName.Lng;
                        newSpeechAddress.Lat_Y = getMarkName.Lat;
                        ShowAddr(1, getMarkName.Address, newSpeechAddress, ref resAddr, getMarkName.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                }
                #endregion

                #region (1)先完整查詢(找圖資)
                var kwAddrOne = kw;
                var ckIsNum = false;
                if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                {
                    // 如果有完整的行政單位，就用解析過的地址去找
                    kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    ckIsNum = true;
                }
                var oneOnlyAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                if (!string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    oneOnlyAddr = getAddr.Dist + oneOnlyAddr;
                }
                var getAddrOne = await GoASRAPI(kwAddrOne, kwAddrOne, markName: addrRemoveOne, onlyAddr: oneOnlyAddr, noCityDist: noCityDist).ConfigureAwait(false);
                resAddr.Address = kwAddrOne;
                if (getAddrOne != null)
                {
                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                    if (getNewAddr.Num == getAddr.Num)
                    {
                        if (ckIsNum) { getAddrOne.Memo = ""; }
                        newSpeechAddress.Lng_X = getAddrOne.Lng;
                        newSpeechAddress.Lat_Y = getAddrOne.Lat;
                        ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(holdNum) && !string.IsNullOrEmpty(getAddr.Num))
                    {
                        getAddrOne = await GoASRAPI(kwAddrOne.Replace(getAddr.Num, holdNum), "").ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    // 有City+Road+Num 就去掉City再找一次
                    if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                    {
                        var tempOnlyAddr = getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        getAddrOne = await GoASRAPI(tempOnlyAddr, tempOnlyAddr, isNum: true, onlyAddr: getAddr.City).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.City == getAddr.City && getNewAddr.Road == getAddr.Road && getNewAddr.Num == getAddr.Num)
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        // 改用組的
                        var tempThesaurusOne = "";
                        if (!string.IsNullOrEmpty(getAddr.City)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Road)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Sect)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Lane)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                        tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Num + ")";
                        getAddrOne = await GoASRAPI("", tempThesaurusOne, isNum: true, onlyAddr: getAddr.Num).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.City == getAddr.City && getNewAddr.Num == getAddr.Num)
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    if ((!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        // 如果有完整的行政單位，上面沒找到 可能是 路/街 念錯了
                        var tempRoad = "";
                        if (!string.IsNullOrEmpty(getAddr.Road))
                        {
                            if (getAddr.Road.IndexOf("路") > -1)
                            {
                                tempRoad = getAddr.Road.Replace("路", "街");
                            }
                            else if (getAddr.Road.IndexOf("街") > -1)
                            {
                                tempRoad = getAddr.Road.Replace("街", "路");
                            }
                        }
                        if (!string.IsNullOrEmpty(tempRoad))
                        {
                            kwAddrOne = getAddr.City + getAddr.Dist + tempRoad + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            oneOnlyAddr = oneOnlyAddr.Replace(getAddr.Road, tempRoad);
                            getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: oneOnlyAddr).ConfigureAwait(false);
                            if (getAddrOne != null)
                            {
                                var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                                if (getNewAddr.Num == getAddr.Num && getNewAddr.Road == tempRoad)
                                {
                                    newSpeechAddress.Lng_X = getAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                    ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                    {
                        kwAddrOne = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: getAddr.City).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.Num == getAddr.Num)
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Lane2))
                        {
                            kwAddrOne = getAddr.Road + getAddr.Sect + getAddr.Lane2 + getAddr.Non + getAddr.Num;
                            getAddrOne = await GoASRAPI("", kwAddrOne, isNum: true, onlyAddr: getAddr.City).ConfigureAwait(false);
                            if (getAddrOne != null)
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    if (ckIsNum && !string.IsNullOrEmpty(getAddr.Lane2))
                    {
                        kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane2 + getAddr.Non + getAddr.Num;
                        getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane2 + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                        resAddr.Address = kwAddrOne;
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    if (!string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.Lane) && getAddr.Num.Replace("號", "") == getAddr.Lane.Replace("巷", ""))
                    {
                        // 有可能是嘴誤
                        kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Non + getAddr.Num;
                        getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    if (getAddr.Num.IndexOf("之") > -1 && !string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        // 有完整行政區+之號 去掉之再找一次
                        kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Non + getAddr.Num.Remove(getAddr.Num.IndexOf("之"));
                        getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    if ((!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        // 如果有City或Dist的行政單位，就用解析過的地址去找
                        var tempkwAddrOne = kwAddrOne;
                        kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        if (tempkwAddrOne != kwAddrOne)
                        {
                            getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                            resAddr.Address = kwAddrOne;
                            if (getAddrOne != null)
                            {
                                var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                                if (getNewAddr.Num == getAddr.Num)
                                {
                                    newSpeechAddress.Lng_X = getAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                    ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist))
                        {
                            // 可能縣市的單位錯了
                            kwAddrOne = (string.IsNullOrEmpty(getAddr.City) ? "" : getAddr.City.Remove(getAddr.City.Length - 1)) + (string.IsNullOrEmpty(getAddr.Dist) ? "" : getAddr.Dist.Remove(getAddr.Dist.Length - 1)) + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            getAddrOne = await GoASRAPI(kwAddrOne, "", isNum: true, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                            if (getAddrOne != null)
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        #region 有可能City念錯
                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist))
                        {
                            kwAddrOne = getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            getAddrOne = await GoASRAPI("", kwAddrOne, isNum: true, onlyAddr: kwAddrOne).ConfigureAwait(false);
                            if (getAddrOne != null)
                            {
                                var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                                if (getNewAddr.Num == getAddr.Num && getNewAddr.Road == getAddr.Road)
                                {
                                    newSpeechAddress.Lng_X = getAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                    ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #endregion
                        #region 有可能Dist念錯
                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist))
                        {
                            kwAddrOne = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            getAddrOne = await GoASRAPI("", kwAddrOne, isNum: true, onlyAddr: getAddr.City).ConfigureAwait(false);
                            if (getAddrOne != null)
                            {
                                var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                                if (getNewAddr.Num == getAddr.Num && getNewAddr.Road == getAddr.Road)
                                {
                                    newSpeechAddress.Lng_X = getAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                    ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #endregion
                        #region 用組的
                        var tempThesaurusOne = "";
                        var tempOnlyAddr = "";
                        if (!string.IsNullOrEmpty(getAddr.City)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Road)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Sect)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; tempOnlyAddr += getAddr.Sect; }
                        if (!string.IsNullOrEmpty(getAddr.Lane))
                        {
                            if (string.IsNullOrEmpty(getAddr.Road))
                            {
                                tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Lane[0..^1] + ") and FORMSOF(THESAURUS,巷) and";
                            }
                            else
                            {
                                tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                            }
                            tempOnlyAddr += getAddr.Lane;
                        }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; tempOnlyAddr += getAddr.Non; }
                        if (string.IsNullOrEmpty(tempOnlyAddr)) { tempOnlyAddr = getAddr.Road + getAddr.Lane; }
                        tempThesaurusOne += " FORMSOF(THESAURUS," + getAddr.Num + ")";
                        getAddrOne = await GoASRAPI("", tempThesaurusOne, isNum: true, onlyAddr: tempOnlyAddr + getAddr.Num).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        #endregion
                    }

                    var _kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    // 如果是完整地址卻沒找到，判斷是否為連音導致語音轉文字的時候誤判 (號需為幾個數字都一樣的才判斷)
                    if (_kwAddrOne == kw && getAddr.Num.Replace("號", "").Length > 1 && AreAllCharactersSame(getAddr.Num.Replace("號", "")))
                    {
                        _kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num[1..];
                        getAddrOne = await GoASRAPI(_kwAddrOne, "", isNum: true, onlyAddr: _kwAddrOne).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }

                    if (_kwAddrOne.Length == kw.Length && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                    {
                        _kwAddrOne = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        getAddrOne = await GoASRAPI(_kwAddrOne, _kwAddrOne, isNum: true, onlyAddr: _kwAddrOne).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }

                    if (kw.IndexOf("號") == -1 && string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && _kwAddrOne == kw + "號")
                    {
                        // 實際上沒有念號，但有唸到數字，移除字母與數字
                        var pattern = "[a-zA-Z0-9]";
                        var getMarkName = Regex.Replace(kw, pattern, "");
                        getAddrOne = await GoASRAPI(getMarkName, "", markName: getMarkName).ConfigureAwait(false);
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(1, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 有號又有巷口等關鍵字 改抓巷口等
                if (getAddOneNum)
                {
                    getAddrOne = await GoASRAPI(getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num.Replace("號", "") + getAddOneNumWord, "", isAddOne: true).ConfigureAwait(false);
                    if (getAddrOne != null)
                    {
                        newSpeechAddress.Lng_X = getAddrOne.Lng;
                        newSpeechAddress.Lat_Y = getAddrOne.Lat;
                        ShowAddr(3, getAddrOne.Address, newSpeechAddress, ref resAddr, crossRoad: getAddOneNumWord);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                }
                #endregion

                #region 沒贅字且有號 純用全文檢索再找一次
                if (!string.IsNullOrEmpty(getAddr.Num) && string.IsNullOrEmpty(addrRemoveOne) && kwAddrOne?.IndexOf("捷運") == -1)
                {
                    var _kw = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    getAddrOne = await GoASRAPI("", kw, onlyAddr: _kw, noCityDist: noCityDist).ConfigureAwait(false);
                    resAddr.Address = kw;
                    if (getAddrOne != null)
                    {
                        newSpeechAddress.Lng_X = getAddrOne.Lng;
                        newSpeechAddress.Lat_Y = getAddrOne.Lat;
                        ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Sect) || !string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getAddr.Non)))
                    {
                        getAddrOne = await GoASRAPI("", _kw, onlyAddr: _kw).ConfigureAwait(false);
                        resAddr.Address = _kw;
                        if (getAddrOne != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 找鄰近的號
                if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                {
                    var strNum = getAddr.Num.Replace("號", "");
                    if (strNum.IndexOf("之") > -1)
                    {
                        strNum = strNum.Split("之")[0];
                        // 去掉"之"號找一次
                        var _addr = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + strNum + "號";
                        getAddrOne = await GoASRAPI(_addr, "", onlyAddr: _addr).ConfigureAwait(false);
                        resAddr.Address = _addr;
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.Num.IndexOf("之") == -1)
                            {
                                // 用之再找一次
                                _addr = getNewAddr.City + getNewAddr.Dist + getNewAddr.Road + getNewAddr.Sect + getNewAddr.Lane + getNewAddr.Non + getAddr.Num;
                                var _getAddrOne = await GoASRAPI(_addr, "", onlyAddr: _addr).ConfigureAwait(false);
                                if (_getAddrOne != null)
                                {
                                    newSpeechAddress.Lng_X = _getAddrOne.Lng;
                                    newSpeechAddress.Lat_Y = _getAddrOne.Lat;
                                    ShowAddr(0, _getAddrOne.Address, newSpeechAddress, ref resAddr, _getAddrOne.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                            if (getNewAddr.Num == strNum + "號")
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }

                    // 號-2找一次
                    var successNum = int.TryParse(strNum, out var _num);
                    if (successNum && _num > 3)
                    {
                        var _addr = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num - 2).ToString() + "號";
                        getAddrOne = await GoASRAPI(_addr, "", onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num - 2).ToString() + "號").ConfigureAwait(false);
                        resAddr.Address = _addr;
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.Num == (_num - 2).ToString() + "號")
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        getAddrOne = await GoASRAPI("", _addr, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num - 2).ToString() + "號").ConfigureAwait(false);
                        resAddr.Address = _addr;
                        if (getAddrOne != null)
                        {
                            var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                            if (getNewAddr.Num == (_num - 2).ToString() + "號")
                            {
                                newSpeechAddress.Lng_X = getAddrOne.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne.Lat;
                                ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }

                    // 號+2找一次
                    var _addr1 = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num + 2).ToString() + "號";
                    getAddrOne = await GoASRAPI(_addr1, "", onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num + 2).ToString() + "號").ConfigureAwait(false);
                    resAddr.Address = _addr1;
                    if (getAddrOne != null)
                    {
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                        if (getNewAddr.Num == (_num + 2).ToString() + "號")
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    getAddrOne = await GoASRAPI("", _addr1, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + (_num + 2).ToString() + "號").ConfigureAwait(false);
                    resAddr.Address = _addr1;
                    if (getAddrOne != null)
                    {
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOne.Address, IsCrossRoads = false });
                        if (getNewAddr.Num == (_num + 2).ToString() + "號")
                        {
                            newSpeechAddress.Lng_X = getAddrOne.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne.Lat;
                            ShowAddr(0, getAddrOne.Address, newSpeechAddress, ref resAddr, getAddrOne.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 移除指定贅字2
                var getExcessWords2 = false;
                foreach (var str in excessWords2)
                {
                    if (kw.IndexOf(str) > -1)
                    {
                        kw = kw.Replace(str, "");
                        addrRemoveOne = addrRemoveOne.Replace(str, "");
                        getExcessWords2 = true;
                    }
                }
                if (getExcessWords2)
                {
                    getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                    var getAddrOne1 = await GoASRAPI(kw, "").ConfigureAwait(false);
                    resAddr.Address = kw;
                    if (getAddrOne1 != null)
                    {
                        if (!checkMarkName && kw.IndexOf("號") == -1 && !string.IsNullOrEmpty(getAddrOne1.Memo))
                        {
                            return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號, 2).ConfigureAwait(false);
                        }
                        newSpeechAddress.Lng_X = getAddrOne1.Lng;
                        newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                        ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, getAddrOne1.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                }
                #endregion

                #region 移除City前的贅字
                if (string.IsNullOrEmpty(getAddr.City))
                {
                    var isGetData = false;
                    foreach (var c in allCity)
                    {
                        var city = c + "市";
                        var arrKw = kw.Split(city);
                        if (arrKw.Length > 1)
                        {
                            if (kw.IndexOf(city) == (kw.Length - city.Length))
                            {
                                // 城市在最後
                                kw = city + arrKw[0];
                                var _getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_getAddr.City))
                                {
                                    getAddr.City = _getAddr.City;
                                }
                            }
                            else
                            {
                                // 不能直接用Replace，因為有可以與道路名相同
                                var idx = kw.IndexOf(arrKw[0]);
                                if (idx > 0)
                                {
                                    kw = kw[(idx + arrKw[0].Length)..];
                                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                                    if (!string.IsNullOrEmpty(_getAddr.City))
                                    {
                                        getAddr.City = _getAddr.City;
                                    }
                                }
                                else
                                {
                                    // 城市在中間
                                    getAddr.City = city;
                                    #region 重取地址
                                    var getAddr1 = SetGISAddress(new SearchGISAddress { Address = arrKw[0], IsCrossRoads = false });
                                    var getAddr2 = SetGISAddress(new SearchGISAddress { Address = arrKw[1], IsCrossRoads = false });
                                    if (string.IsNullOrEmpty(getAddr.Dist))
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Dist)) { getAddr.Dist = getAddr1.Dist; }
                                        if (!string.IsNullOrEmpty(getAddr2.Dist)) { getAddr.Dist = getAddr2.Dist; }
                                    }
                                    if (string.IsNullOrEmpty(getAddr.Road) || getAddr.Road?.IndexOf(city) > -1)
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Road)) { getAddr.Road = getAddr1.Road; }
                                        if (!string.IsNullOrEmpty(getAddr2.Road)) { getAddr.Road = getAddr2.Road; }
                                    }
                                    if (string.IsNullOrEmpty(getAddr.Sect))
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Sect)) { getAddr.Sect = getAddr1.Sect; }
                                        if (!string.IsNullOrEmpty(getAddr2.Sect)) { getAddr.Sect = getAddr2.Sect; }
                                    }
                                    if (string.IsNullOrEmpty(getAddr.Lane))
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Lane)) { getAddr.Lane = getAddr1.Lane; }
                                        if (!string.IsNullOrEmpty(getAddr2.Lane)) { getAddr.Lane = getAddr2.Lane; }
                                    }
                                    if (string.IsNullOrEmpty(getAddr.Non))
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Non)) { getAddr.Non = getAddr1.Non; }
                                        if (!string.IsNullOrEmpty(getAddr2.Non)) { getAddr.Non = getAddr2.Non; }
                                    }
                                    if (string.IsNullOrEmpty(getAddr.Num))
                                    {
                                        if (!string.IsNullOrEmpty(getAddr1.Num)) { getAddr.Num = getAddr1.Num; }
                                        if (!string.IsNullOrEmpty(getAddr2.Num)) { getAddr.Num = getAddr2.Num; }
                                    }
                                    #endregion
                                }

                            }
                            isGetData = true;
                            break;
                        }
                    }
                    if (!isGetData)
                    {
                        foreach (var c in allCityCounty)
                        {
                            var city = c + "縣";
                            var arrKw = kw.Split(city);
                            if (arrKw.Length > 1)
                            {
                                if (kw.IndexOf(city) == (kw.Length - city.Length))
                                {
                                    // 城市在最後
                                    kw = city + arrKw[0];
                                    var _getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                                    if (!string.IsNullOrEmpty(_getAddr.City))
                                    {
                                        getAddr.City = _getAddr.City;
                                    }
                                }
                                else
                                {
                                    var idx = kw.IndexOf(arrKw[0]);
                                    if (idx > 0)
                                    {
                                        kw = kw[(idx + arrKw[0].Length)..];
                                    }
                                    else
                                    {
                                        // 城市在中間
                                        getAddr.City = city;
                                        #region 重取地址
                                        var getAddr1 = SetGISAddress(new SearchGISAddress { Address = arrKw[0], IsCrossRoads = false });
                                        var getAddr2 = SetGISAddress(new SearchGISAddress { Address = arrKw[1], IsCrossRoads = false });
                                        if (string.IsNullOrEmpty(getAddr.Dist))
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Dist)) { getAddr.Dist = getAddr1.Dist; }
                                            if (!string.IsNullOrEmpty(getAddr2.Dist)) { getAddr.Dist = getAddr2.Dist; }
                                        }
                                        if (string.IsNullOrEmpty(getAddr.Road) || getAddr.Road.IndexOf(city) > -1)
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Road)) { getAddr.Road = getAddr1.Road; }
                                            if (!string.IsNullOrEmpty(getAddr2.Road)) { getAddr.Road = getAddr2.Road; }
                                        }
                                        if (string.IsNullOrEmpty(getAddr.Sect))
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Sect)) { getAddr.Sect = getAddr1.Sect; }
                                            if (!string.IsNullOrEmpty(getAddr2.Sect)) { getAddr.Sect = getAddr2.Sect; }
                                        }
                                        if (string.IsNullOrEmpty(getAddr.Lane))
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Lane)) { getAddr.Lane = getAddr1.Lane; }
                                            if (!string.IsNullOrEmpty(getAddr2.Lane)) { getAddr.Lane = getAddr2.Lane; }
                                        }
                                        if (string.IsNullOrEmpty(getAddr.Non))
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Non)) { getAddr.Non = getAddr1.Non; }
                                            if (!string.IsNullOrEmpty(getAddr2.Non)) { getAddr.Non = getAddr2.Non; }
                                        }
                                        if (string.IsNullOrEmpty(getAddr.Num))
                                        {
                                            if (!string.IsNullOrEmpty(getAddr1.Num)) { getAddr.Num = getAddr1.Num; }
                                            if (!string.IsNullOrEmpty(getAddr2.Num)) { getAddr.Num = getAddr2.Num; }
                                        }
                                        #endregion
                                    }
                                }
                                isGetData = true;
                                break;
                            }
                        }
                    }
                    if (!isGetData)
                    {
                        foreach (var c in allCityTwo)
                        {
                            var city1 = c + "市";
                            var city2 = c + "縣";
                            var arrKw1 = kw.Split(city1);
                            var arrKw2 = kw.Split(city2);
                            if (arrKw1.Length > 1)
                            {
                                var idx = kw.IndexOf(arrKw1[0]);
                                kw = kw[(idx + arrKw1[0].Length)..];
                                var _getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_getAddr.City))
                                {
                                    getAddr.City = _getAddr.City;
                                }
                                break;
                            }
                            if (arrKw2.Length > 1)
                            {
                                var idx = kw.IndexOf(arrKw2[0]);
                                kw = kw[(idx + arrKw2[0].Length)..];
                                var _getAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                                if (!string.IsNullOrEmpty(_getAddr.City))
                                {
                                    getAddr.City = _getAddr.City;
                                }
                                break;
                            }
                        }
                    }
                    resAddr.Address = kw;
                }
                #endregion

                #region 沒有地址只有不確定是否為贅字的字
                if (string.IsNullOrEmpty(addrComb))
                {
                    if (addrRemoveOne.Length > 0)
                    {
                        var newKw = kw;
                        foreach (var m in markNameReplace)
                        {
                            var arrM = m.Split("|");
                            newKw = newKw.Replace(arrM[0], arrM[1]);
                        }
                        if (kw.Trim() != newKw.Trim())
                        {
                            var getAddrOne1 = await GoASRAPI(newKw, "").ConfigureAwait(false);
                            resAddr.Address = newKw;
                            if (getAddrOne1 != null)
                            {
                                newSpeechAddress.Lng_X = getAddrOne1.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                                ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, getAddrOne1.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        // 判斷有沒有"的"
                        var arrData = kw.Split("的");
                        if (arrData.Length > 1)
                        {
                            var getAddrOne2 = await GoASRAPI("", "FORMSOF(THESAURUS," + arrData[0] + ") and FORMSOF(THESAURUS," + arrData[1] + ")").ConfigureAwait(false);
                            resAddr.Address = kw;
                            if (getAddrOne2 != null)
                            {
                                newSpeechAddress.Lng_X = getAddrOne2.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne2.Lat;
                                ShowAddr(0, getAddrOne2.Address, newSpeechAddress, ref resAddr, getAddrOne2.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        // 檢查是否為捷運
                        if (addrRemoveOne?.IndexOf("捷運站") > -1)
                        {
                            var newAddr = addrRemoveOne;
                            var arrKw = addrRemoveOne.Split("捷運站");
                            if (arrKw.Length > 2 && !string.IsNullOrEmpty(arrKw[1]))
                            {
                                newAddr = "捷運" + arrKw[1] + "站";
                            }
                            else
                            {
                                newAddr = "捷運" + arrKw[0] + "站";
                            }
                            var getAddrOne2 = await GoASRAPI(newAddr, "", markName: newAddr, noCityDist: true).ConfigureAwait(false);
                            if (getAddrOne2 != null)
                            {
                                newSpeechAddress.Lng_X = getAddrOne2.Lng;
                                newSpeechAddress.Lat_Y = getAddrOne2.Lat;
                                ShowAddr(1, getAddrOne2.Address, newSpeechAddress, ref resAddr, getAddrOne2.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    return await SpeechAddressResponse(resAddr, 500, checkMarkName ? CallReasonEnum.無法判定地址 : CallReasonEnum.乘客問題, 2).ConfigureAwait(false);
                }
                else
                {
                    // 判斷有沒有"在"+"的"
                    var arrData = kw.Split("在");
                    var arrData1 = kw.Split("的");
                    if (arrData.Length > 1 && arrData1.Length > 1)
                    {
                        var getAddrOne1 = await GoASRAPI("", "FORMSOF(THESAURUS," + arrData[1].Split("的")[0] + ") and FORMSOF(THESAURUS," + arrData1[1] + ")").ConfigureAwait(false);
                        resAddr.Address = kw;
                        if (getAddrOne1 != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne1.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                            ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, getAddrOne1.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                bool? _isNum = null;
                var _markName = "";

                #region 判斷是否只有路沒有城市，移除可能的贅字
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)))
                {
                    if (getAddr.Road.Any(x => x.ToString() == "里"))
                    {
                        var idx = getAddr.Road.IndexOf("里");
                        var newRoad = getAddr.Road[(idx + 1)..];
                        var getNewRoad = SetGISAddress(new SearchGISAddress { Address = newRoad, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getNewRoad.Road)) { getAddr.Road = getNewRoad.Road; }
                    }
                    if (getAddr.Road.Any(x => x.ToString() == "村"))
                    {
                        var idx = getAddr.Road.IndexOf("村");
                        var newRoad = getAddr.Road[(idx + 1)..];
                        var getNewRoad = SetGISAddress(new SearchGISAddress { Address = newRoad, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(getNewRoad.Road)) { getAddr.Road = getNewRoad.Road; }
                    }
                    if (!string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.IndexOf("新莊") == 0 && getAddr.Road.IndexOf("新莊區") == -1 && getAddr.Road.IndexOf("新莊街") == -1 && getAddr.Road.IndexOf("新莊路") == -1)
                    {
                        getAddr.Road = getAddr.Road.Replace("新莊", "");
                        getAddr.Dist = "新莊區";
                    }
                }
                #endregion

                #region 判斷路是否正確
                if (!string.IsNullOrEmpty(getAddr.Road))
                {
                    // 如果路只有兩碼必須要有City或Dist
                    if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && getAddr.Road.Trim().Length == 2)
                    {
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                    }
                    var getNewRoad = SetGISAddress(new SearchGISAddress { Address = getAddr.Road, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getNewRoad.Road))
                    {
                        // 判斷是否有兩條路
                        if (getAddr.Road != getNewRoad.Road)
                        {
                            var getNewRoad1 = SetGISAddress(new SearchGISAddress { Address = getAddr.Road.Replace(getNewRoad.Road, ""), IsCrossRoads = false });
                            if (getNewRoad1.Road != getNewRoad.Road)
                            {
                                getAddr.Road2 = getNewRoad1.Road;
                            }
                        }
                        getAddr.Road = getNewRoad.Road;
                    }
                    if (!string.IsNullOrEmpty(getNewRoad.Sect)) { getAddr.Sect = getNewRoad.Sect; }
                    if (!string.IsNullOrEmpty(getNewRoad.Lane)) { getAddr.Lane = getNewRoad.Lane; }
                    if (!string.IsNullOrEmpty(getNewRoad.Non)) { getAddr.Non = getNewRoad.Non; }
                }
                #endregion

                #region 判斷段是否正確
                if (!string.IsNullOrEmpty(getAddr.Sect) && getAddr.Sect.Length > 5)
                {
                    foreach (var str in noNumKeyWord)
                    {
                        if (getAddr.Sect.IndexOf(str) > -1)
                        {
                            var _newAddr = getAddr.Sect.Remove(getAddr.Sect.IndexOf(str)) + str.Substring(0, 1);
                            var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = _newAddr, IsCrossRoads = false });
                            if (string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getOtherAddr.Road))
                            {
                                getAddr.Road = getOtherAddr.Road;
                            }
                            if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getOtherAddr.Lane))
                            {
                                getAddr.Lane = getOtherAddr.Lane;
                            }
                            if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getOtherAddr.Non))
                            {
                                getAddr.Non = getOtherAddr.Non;
                            }
                            getAddr.Sect = "";
                            break;
                        }
                    }
                }
                #endregion

                #region 移除重複的單位
                if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    getAddr.Road = getAddr.Road.Replace(getAddr.Num, "");
                }
                if (!string.IsNullOrEmpty(getAddr.Sect) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    getAddr.Sect = getAddr.Sect.Replace(getAddr.Num, "");
                }
                if (!string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    getAddr.Lane = getAddr.Lane.Replace(getAddr.Num, "");
                }
                if (!string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    getAddr.Non = getAddr.Non.Replace(getAddr.Num, "");
                }
                if (!string.IsNullOrEmpty(getAddr.City))
                {
                    // 檢查是否有重複的City
                    var arrCity = kw.Split(getAddr.City);
                    if (arrCity.Length > 2)
                    {
                        var _addr = getAddr.City + arrCity[^1];
                        var _getNewAddr = SetGISAddress(new SearchGISAddress { Address = _addr, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(_getNewAddr.Dist)) { getAddr.Dist = _getNewAddr.Dist; }
                        if (!string.IsNullOrEmpty(_getNewAddr.Road)) { getAddr.Road = _getNewAddr.Road; }
                        if (!string.IsNullOrEmpty(_getNewAddr.Sect)) { getAddr.Sect = _getNewAddr.Sect; }
                        if (!string.IsNullOrEmpty(_getNewAddr.Lane)) { getAddr.Lane = _getNewAddr.Lane; }
                        if (!string.IsNullOrEmpty(_getNewAddr.Non)) { getAddr.Non = _getNewAddr.Non; }
                        if (!string.IsNullOrEmpty(_getNewAddr.Num)) { getAddr.Num = _getNewAddr.Num; }
                    }
                }
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road))
                {
                    // 檢查Road是否有重複字串
                    var newRoad = "";
                    var arr = new List<string>();
                    var arrRoad = getAddr.Road.ToCharArray().OrderBy(x => x).ToArray();
                    for (var i = 1; i < arrRoad.Count(); i++)
                    {
                        if (arrRoad[i] == arrRoad[i - 1])
                        {
                            arr.Add(arrRoad[i].ToString());
                        }
                    }
                    foreach (var a in arr)
                    {
                        // 只移除第一個重複的字元
                        var idx = getAddr.Road.IndexOf(a);
                        newRoad = getAddr.Road.Substring(0, idx) + getAddr.Road[(idx + 1)..];
                        break;
                    }
                    if (!string.IsNullOrEmpty(newRoad))
                    {
                        var newAddrComb = getAddr.City + getAddr.Dist + newRoad + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        var getAddrOne1 = await GoASRAPI("", newAddrComb).ConfigureAwait(false);
                        resAddr.Address = kw;
                        if (getAddrOne1 != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne1.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                            ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, getAddrOne1.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 判斷是不是區順序錯誤
                var addrRemoveTow = kw;
                #region 找出已知地址外的贅字
                if (!string.IsNullOrEmpty(getAddr.City)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.City, ""); }
                if (!string.IsNullOrEmpty(getAddr.Dist)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Dist, ""); }
                if (!string.IsNullOrEmpty(getAddr.Road)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Road, ""); }
                if (!string.IsNullOrEmpty(getAddr.Sect)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Sect, ""); }
                if (!string.IsNullOrEmpty(getAddr.Lane)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Lane, ""); }
                if (!string.IsNullOrEmpty(getAddr.Non)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Non, ""); }
                if (!string.IsNullOrEmpty(getAddr.Num)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Num, ""); }
                #endregion
                if (string.IsNullOrEmpty(getAddr.Dist))
                {
                    var getDist = SetGISAddress(new SearchGISAddress { Address = addrRemoveTow, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getDist.Dist))
                    {
                        getAddr.Dist = getDist.Dist;
                    }
                }
                if (string.IsNullOrEmpty(getAddr.Num) && kw.IndexOf("號") > -1)
                {
                    // 有號但拆解不到 可能有重複的資料導致拆解錯誤
                    if (!string.IsNullOrEmpty(getAddr.Lane))
                    {
                        var arrLane = kw.Split(getAddr.Lane);
                        if (arrLane.Length > 2)
                        {
                            foreach (var t in arrLane)
                            {
                                if (t.IndexOf("號") > -1)
                                {
                                    var getNewNum = SetGISAddress(new SearchGISAddress { Address = t, IsCrossRoads = false });
                                    getAddr.Num = getNewNum.Num;
                                }
                            }
                        }
                    }
                }
                #endregion

                #region 檢查是否City或Dist被放到Road或Lane了
                var getDistList = await GetDists(new DistRequest { City = "" }).ConfigureAwait(false);
                if ((string.IsNullOrEmpty(getAddr.City) || string.IsNullOrEmpty(getAddr.Dist)) && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane))
                    && getAddr.Road != "基隆路" && !getAddr.Road.StartsWith("中山") && !getAddr.Road.StartsWith("中正"))
                {
                    var twoWord = !string.IsNullOrEmpty(getAddr.Road) ? (getAddr.Road.Length > 1 ? getAddr.Road.Substring(0, 2) : "") : (getAddr.Lane.Length > 1 ? getAddr.Lane.Substring(0, 2) : ""); // 取前兩字
                    var isGetData = false;

                    #region 檢查City
                    foreach (var c in allCity)
                    {
                        if (getAddr.Road == (c + "路") || getAddr.Road == (c + "街"))
                        {
                            break;
                        }
                        if ((twoWord) == c)
                        {
                            isGetData = true;
                            getAddr.City = c + "市";
                            break;
                        }
                    }
                    if (!isGetData)
                    {
                        foreach (var c in allCityCounty)
                        {
                            if (getAddr.Road == (c + "路") || getAddr.Road == (c + "街"))
                            {
                                break;
                            }
                            if ((twoWord) == c)
                            {
                                isGetData = true;
                                getAddr.City = c + "縣";
                                break;
                            }
                        }
                    }
                    if (!isGetData)
                    {
                        foreach (var c in allCityTwo)
                        {
                            if (getAddr.Road == (c + "路") || getAddr.Road == (c + "街"))
                            {
                                break;
                            }
                            if ((twoWord) == c)
                            {
                                isGetData = true;
                                getAddr.City = c + "縣";
                                break;
                            }
                        }
                    }
                    if (isGetData)
                    {
                        if (!string.IsNullOrEmpty(getAddr.Road))
                        {
                            getAddr.Road = getAddr.Road[2..];
                        }
                        else
                        {
                            getAddr.Lane = getAddr.Lane[2..];
                        }
                        twoWord = !string.IsNullOrEmpty(getAddr.Road) ? (getAddr.Road.Length > 1 ? getAddr.Road.Substring(0, 2) : "") : (getAddr.Lane.Length > 1 ? getAddr.Lane.Substring(0, 2) : ""); // 取前兩字
                    }
                    #endregion

                    #region 檢查Dist
                    var deDist = "區";
                    var _getDist = getDistList.Where(x => x.Substring(0, 2) == twoWord).FirstOrDefault();
                    if (_getDist != null)
                    {
                        foreach (var d in allDist)
                        {
                            if (_getDist.EndsWith(d))
                            {
                                deDist = d;
                                break;
                            }
                        }
                        if (getAddr.Road?.IndexOf(deDist) > -1 || getAddr.Lane?.IndexOf(deDist) > -1) { _getDist = null; }
                    }
                    if (!string.IsNullOrEmpty(getAddr.Dist) && _getDist != null && getAddr.Dist.Substring(0, 2) != _getDist)
                    {
                        _getDist = null;
                    }
                    // 若有City，需檢查找到的Dist是否屬於該City
                    if (_getDist != null && !string.IsNullOrEmpty(getAddr.City))
                    {
                        var getDist = (await GetDists(new DistRequest { City = getAddr.City }).ConfigureAwait(false)).Where(x => x == _getDist).FirstOrDefault();
                        if (getDist == null) { _getDist = null; }
                    }
                    if (_getDist != null && getAddr.Road != twoWord + "路" && getAddr.Road != twoWord + "街")
                    {
                        getAddr.Dist = _getDist;
                        if (getAddr.Road.Length > 4)
                        {
                            var orgRoad = getAddr.Road;
                            getAddr.Road = getAddr.Road.Replace(_getDist, "").Replace(_getDist.Substring(0, 2), "");
                            kw = kw.Replace(orgRoad, getAddr.Road);
                        }
                        if (getAddr.Lane.Length > 4)
                        {
                            var orgLane = getAddr.Lane;
                            getAddr.Lane = getAddr.Lane.Replace(_getDist, "").Replace(_getDist.Substring(0, 2), "");
                            kw = kw.Replace(orgLane, getAddr.Lane);
                        }
                    }
                    else
                    {
                        #region 檢查Road是否含"區"+贅字
                        if (!string.IsNullOrEmpty(getAddr.Road))
                        {
                            var _getCounty = -1;
                            foreach (var d in allDist)
                            {
                                _getCounty = getAddr.Road.IndexOf(d);
                                if (_getCounty > -1) { break; }
                            }
                            foreach (var r in distRoad)
                            {
                                if (getAddr.Road.IndexOf(r) > -1)
                                {
                                    // 有找到關鍵字就不判斷
                                    _getCounty = -1;
                                    break;
                                }
                            }
                            if (_getCounty > 0)
                            {
                                if (_getCounty == 1)
                                {
                                    var _dist2 = getDistList.Where(x => x == getAddr.Road.Substring(_getCounty - 1, 2)).FirstOrDefault();
                                    if (_dist2 != null)
                                    {
                                        getAddr.Dist = _dist2;
                                    }
                                    else
                                    {
                                        getAddr.Dist = "";
                                    }
                                }
                                if (_getCounty > 1)
                                {
                                    var _dist3 = getDistList.Where(x => x == getAddr.Road.Substring(_getCounty - 2, 3)).FirstOrDefault();
                                    if (_dist3 != null)
                                    {
                                        getAddr.Dist = _dist3;
                                    }
                                    else
                                    {
                                        getAddr.Dist = "";
                                    }
                                }
                                var orgRoad = getAddr.Road;
                                getAddr.Road = getAddr.Road.Substring(_getCounty + 1, getAddr.Road.Length - _getCounty - 1);
                                kw = kw.Replace(orgRoad, getAddr.Road);
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Lane))
                        {
                            var _getCounty = -1;
                            foreach (var d in allDist)
                            {
                                _getCounty = getAddr.Lane.IndexOf(d);
                                if (_getCounty > -1) { break; }
                            }
                            if (_getCounty > -1)
                            {
                                if (_getCounty == 1)
                                {
                                    var _dist2 = getDistList.Where(x => x == getAddr.Lane.Substring(_getCounty - 1, 2)).FirstOrDefault();
                                    if (_dist2 != null)
                                    {
                                        getAddr.Dist = _dist2;
                                    }
                                    else
                                    {
                                        getAddr.Dist = "";
                                    }
                                }
                                if (_getCounty > 1)
                                {
                                    var _dist3 = getDistList.Where(x => x == getAddr.Lane.Substring(_getCounty - 2, 3)).FirstOrDefault();
                                    if (_dist3 != null)
                                    {
                                        getAddr.Dist = _dist3;
                                    }
                                    else
                                    {
                                        getAddr.Dist = "";
                                    }
                                }
                                var orgLane = getAddr.Lane;
                                getAddr.Lane = getAddr.Lane.Substring(_getCounty + 1, getAddr.Lane.Length - _getCounty - 1);
                                kw = kw.Replace(orgLane, getAddr.Lane);
                            }
                        }
                        #endregion
                    }
                    #endregion
                }
                #endregion

                #region 檢查是否 City 或 Dist 是否錯誤
                if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist))
                {
                    var getDists = await GetDists(new DistRequest { City = getAddr.City }).ConfigureAwait(false);
                    var getDist = getDists.Where(x => x == getAddr.Dist).FirstOrDefault();
                    if (getDist == null || getDist.Count() == 0)
                    {
                        // 代表City/Dist其中一個單位是錯的
                        var _addr1 = kw.Replace(getAddr.City, "");
                        var getAddrOne1 = await GoASRAPI(_addr1, "").ConfigureAwait(false);
                        resAddr.Address = _addr1;
                        if (getAddrOne1 != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne1.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                            ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, checkMarkName ? getAddrOne1.Memo : "");
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        _addr1 = kw.Replace(getAddr.Dist, "");
                        getAddrOne1 = await GoASRAPI(kw.Replace(getAddr.Dist, ""), "").ConfigureAwait(false);
                        resAddr.Address = _addr1;
                        if (getAddrOne1 != null)
                        {
                            newSpeechAddress.Lng_X = getAddrOne1.Lng;
                            newSpeechAddress.Lat_Y = getAddrOne1.Lat;
                            ShowAddr(0, getAddrOne1.Address, newSpeechAddress, ref resAddr, getAddrOne1.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist))
                {
                    // 檢查是否區被放到成市了
                    var getCity = false;
                    var theCity = getAddr.City.Substring(0, 2);
                    var allCitys = allCity.Concat(allCityCounty).Concat(allCityTwo).ToArray();
                    foreach (var c in allCitys)
                    {
                        if (c == theCity)
                        {
                            getCity = true;
                            break;
                        }
                    }
                    if (!getCity)
                    {
                        var getDist = getDistList.Where(x => x.Contains(theCity)).ToList();
                        if (getDist.Count > 0)
                        {
                            kw = kw.Replace(getAddr.City, getDist.FirstOrDefault());
                            getAddr.City = "";
                            getAddr.Dist = getDist.FirstOrDefault();
                        }
                    }
                }
                #endregion

                #region 組全文檢索字串 組地址
                var thesaurusOne = "";

                // 沒號且有贅字 檢查是否為特殊地標 需要替換關鍵字
                var isMarkName = false;
                if (string.IsNullOrEmpty(getAddr.Num) && addrRemoveOne.Length > 0)
                {
                    foreach (var m in markNameReplace)
                    {
                        var arrM = m.Split("|");
                        var newOtherWord = addrRemoveOne.Replace(arrM[0], arrM[1]);
                        if (newOtherWord.Length != addrRemoveOne.Length)
                        {
                            addrRemoveOne = newOtherWord;
                            isMarkName = true;
                        }
                    }
                }

                #region 不是特殊地標 如果有路沒號 但有"路口"等關鍵字：巷口等 (用數字少的門牌號的去查)
                if (!isMarkName && (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)) && (string.IsNullOrEmpty(getAddr.Num) || (addOneNum && getAddr.Num == "1號")))
                {
                    foreach (var str in oneNumReplace)
                    {
                        var arrM = str.Split("|");
                        if (kw.IndexOf(arrM[0]) > -1)
                        {
                            if (addOneNum)
                            {
                                var _word = arrM[1];
                                if (string.IsNullOrEmpty(_word))
                                {
                                    _word = arrM[0].Substring(0, 1);
                                }
                                if (!string.IsNullOrEmpty(arrM[1]))
                                {
                                    var tempKw = kw.Replace(arrM[0], arrM[1]);
                                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = tempKw, IsCrossRoads = false });
                                    if (!string.IsNullOrEmpty(getNewAddr.Road)) { getAddr.Road = getNewAddr.Road; }
                                    if (!string.IsNullOrEmpty(getNewAddr.Sect)) { getAddr.Sect = getNewAddr.Sect; }
                                    if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getNewAddr.Lane)) { getAddr.Lane = getNewAddr.Lane; }
                                    if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non)) { getAddr.Non = getNewAddr.Non; }
                                }
                                var newKw = "";
                                var newThesaurus = "";
                                var newThesaurusNoN = "";
                                if (!string.IsNullOrEmpty(getAddr.City)) { newKw += getAddr.City; newThesaurus += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Dist)) { newKw += getAddr.Dist; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Road))
                                {
                                    newKw += getAddr.Road;
                                    if (getAddr.Road.Length > 4)
                                    {
                                        newThesaurus += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                        newThesaurus += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                        newThesaurus += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                        newThesaurus += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                                    }
                                    else if (getAddr.Road.Length == 4)
                                    {
                                        newThesaurus += " (FORMSOF(THESAURUS," + getAddr.Road + ") or FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ")) and";
                                    }
                                    else
                                    {
                                        if (!string.IsNullOrEmpty(getAddr.Road2))
                                        {
                                            newThesaurus += " (FORMSOF(THESAURUS," + getAddr.Road + ") or FORMSOF(THESAURUS," + getAddr.Road2 + ")) and";
                                        }
                                        else
                                        {
                                            newThesaurus += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                                        }
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Sect)) { newKw += getAddr.Sect; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Lane)) { newKw += getAddr.Lane; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Non)) { newKw += getAddr.Non; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                newThesaurusNoN = newThesaurus.Remove(newThesaurus.Length - 3, 3).Trim();
                                newKw += "1號";// 加1號去查
                                newThesaurus += " FORMSOF(THESAURUS,1號) ";
                                var getAddrOneNum = await GoASRAPI(newKw, newThesaurus, isNum: true, isAddOne: true).ConfigureAwait(false);
                                resAddr.Address = newKw;
                                if (getAddrOneNum != null)
                                {
                                    var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOneNum.Address, IsCrossRoads = false });
                                    if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non))
                                    {
                                        getAddrOneNum.Address = getAddrOneNum.Address.Replace(getNewAddr.Non, "");
                                    }
                                    newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                    var isNum = false;
                                    if ((!string.IsNullOrEmpty(getAddr.Non) || !string.IsNullOrEmpty(getNewAddr.Non)) && getAddr.Non != getNewAddr.Non)
                                    {
                                        if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Non.Replace("弄", ""))
                                        {
                                            isNum = true;
                                        }
                                    }
                                    if ((!string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getNewAddr.Lane)) && getAddr.Lane != getNewAddr.Lane)
                                    {
                                        if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Lane.Replace("巷", ""))
                                        {
                                            isNum = true;
                                        }
                                    }
                                    if ((!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getNewAddr.Road) && getAddr.Road != getNewAddr.Road) ||
                                           (!string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getNewAddr.Lane) && getAddr.Lane != getNewAddr.Lane) ||
                                           (!string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non) && getAddr.Non != getNewAddr.Non))
                                    {
                                        // 找出不一樣的要重找
                                        var _getAddrOneNum = await GoASRAPI(newKw.Replace("1號", ""), "").ConfigureAwait(false);
                                        if (_getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = _getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = _getAddrOneNum.Lat;
                                            ShowAddr(3, _getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                    }
                                    if (isNum)
                                    {
                                        // 沒有該巷口或弄口 直接顯示門牌
                                        ShowAddr(0, getAddrOneNum.Address, newSpeechAddress, ref resAddr);
                                    }
                                    else
                                    {
                                        ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                    }
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                else
                                {
                                    #region 去掉1號取最小值
                                    newKw = newKw.Replace("1號", "");
                                    getAddrOneNum = await GoASRAPI(newKw, "", isNum: true, isAddOne: true).ConfigureAwait(false);
                                    resAddr.Address = newKw;
                                    if (getAddrOneNum != null)
                                    {
                                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOneNum.Address, IsCrossRoads = false });
                                        if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non))
                                        {
                                            getAddrOneNum.Address = getAddrOneNum.Address.Replace(getNewAddr.Non, "");
                                        }
                                        newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                        newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                        var isNum = false;
                                        if ((!string.IsNullOrEmpty(getAddr.Non) || !string.IsNullOrEmpty(getNewAddr.Non)) && getAddr.Non != getNewAddr.Non)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Non.Replace("弄", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if ((!string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getNewAddr.Lane)) && getAddr.Lane != getNewAddr.Lane)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Lane.Replace("巷", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if (isNum)
                                        {
                                            ShowAddr(0, getAddrOneNum.Address, newSpeechAddress, ref resAddr);
                                        }
                                        else
                                        {
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                        }
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                    getAddrOneNum = await GoASRAPI("", newKw, isNum: true, isAddOne: true).ConfigureAwait(false);
                                    resAddr.Address = newKw;
                                    if (getAddrOneNum != null)
                                    {
                                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOneNum.Address, IsCrossRoads = false });
                                        if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non))
                                        {
                                            getAddrOneNum.Address = getAddrOneNum.Address.Replace(getNewAddr.Non, "");
                                        }
                                        newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                        newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                        var isNum = false;
                                        if ((!string.IsNullOrEmpty(getAddr.Non) || !string.IsNullOrEmpty(getNewAddr.Non)) && getAddr.Non != getNewAddr.Non)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Non.Replace("弄", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if ((!string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getNewAddr.Lane)) && getAddr.Lane != getNewAddr.Lane)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Lane.Replace("巷", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if (isNum)
                                        {
                                            ShowAddr(0, getAddrOneNum.Address, newSpeechAddress, ref resAddr);
                                        }
                                        else
                                        {
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                        }
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                    getAddrOneNum = await GoASRAPI("", newThesaurusNoN, isNum: true, isAddOne: true).ConfigureAwait(false);
                                    resAddr.Address = newKw;
                                    if (getAddrOneNum != null)
                                    {
                                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrOneNum.Address, IsCrossRoads = false });
                                        if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNewAddr.Non))
                                        {
                                            getAddrOneNum.Address = getAddrOneNum.Address.Replace(getNewAddr.Non, "");
                                        }
                                        newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                        newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                        var isNum = false;
                                        if ((!string.IsNullOrEmpty(getAddr.Non) || !string.IsNullOrEmpty(getNewAddr.Non)) && getAddr.Non != getNewAddr.Non)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Non.Replace("弄", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if ((!string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getNewAddr.Lane)) && getAddr.Lane != getNewAddr.Lane)
                                        {
                                            if (getNewAddr.Num.Split("之")[0].Replace("號", "") == getAddr.Lane.Replace("巷", ""))
                                            {
                                                isNum = true;
                                            }
                                        }
                                        if (isNum)
                                        {
                                            ShowAddr(0, getAddrOneNum.Address, newSpeechAddress, ref resAddr);
                                        }
                                        else
                                        {
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                        }
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                    #endregion

                                    #region 可能區念錯
                                    if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist))
                                    {
                                        newThesaurus = "";
                                        newKw = "";
                                        if (!string.IsNullOrEmpty(getAddr.City)) { newKw += getAddr.City; newThesaurus += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Road)) { newKw += getAddr.Road; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Sect)) { newKw += getAddr.Sect; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Lane)) { newKw += getAddr.Lane; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Non)) { newKw += getAddr.Non; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                        newThesaurus = newThesaurus.Remove(newThesaurus.Length - 3, 3).Trim();
                                        getAddrOneNum = await GoASRAPI("", newKw, isAddOne: true).ConfigureAwait(false);
                                        if (getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                        getAddrOneNum = await GoASRAPI("", newThesaurus, isAddOne: true).ConfigureAwait(false);
                                        if (getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Dist))
                                    {
                                        newThesaurus = "";
                                        newKw = "";
                                        if (!string.IsNullOrEmpty(getAddr.Road)) { newKw += getAddr.Road; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Sect)) { newKw += getAddr.Sect; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Lane)) { newKw += getAddr.Lane; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                                        if (!string.IsNullOrEmpty(getAddr.Non)) { newKw += getAddr.Non; newThesaurus += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                        newThesaurus = newThesaurus.Remove(newThesaurus.Length - 3, 3).Trim();
                                        getAddrOneNum = await GoASRAPI("", newKw, isAddOne: true).ConfigureAwait(false);
                                        if (getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                        getAddrOneNum = await GoASRAPI("", newThesaurus, isAddOne: true).ConfigureAwait(false);
                                        if (getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                    }
                                    #endregion

                                    if (getAddr.Road.Length >= 4)
                                    {
                                        if (getAddr.Road.Length > 4)
                                        {
                                            newKw = getAddr.City + getAddr.Dist + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + getAddr.Sect + getAddr.Lane + getAddr.Non;
                                            getAddrOneNum = await GoASRAPI(newKw, "", isAddOne: true).ConfigureAwait(false);
                                            resAddr.Address = newKw;
                                            if (getAddrOneNum != null)
                                            {
                                                newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                                newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                                ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                            }
                                        }
                                        newKw = getAddr.City + getAddr.Dist + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + getAddr.Sect + getAddr.Lane + getAddr.Non;
                                        getAddrOneNum = await GoASRAPI(newKw, "", isAddOne: true).ConfigureAwait(false);
                                        resAddr.Address = newKw;
                                        if (getAddrOneNum != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                            newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                            ShowAddr(3, getAddrOneNum.Address, newSpeechAddress, ref resAddr, crossRoad: _word);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                    }
                                }
                                return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 4).ConfigureAwait(false);
                            }
                            else
                            {
                                #region 同遠傳一樣，直接加1號去找，沒找到就回空
                                var newKw = "";
                                if (!string.IsNullOrEmpty(getAddr.City)) { newKw += getAddr.City; }
                                if (!string.IsNullOrEmpty(getAddr.Dist)) { newKw += getAddr.Dist; }
                                if (!string.IsNullOrEmpty(getAddr.Road)) { newKw += getAddr.Road; }
                                if (!string.IsNullOrEmpty(getAddr.Sect)) { newKw += getAddr.Sect; }
                                if (!string.IsNullOrEmpty(getAddr.Lane)) { newKw += getAddr.Lane; }
                                if (!string.IsNullOrEmpty(getAddr.Non)) { newKw += getAddr.Non; }
                                newKw += "1號";// 加1號去查

                                var getAddrOneNum = await GoASRAPI(newKw, "", isNum: true).ConfigureAwait(false);
                                resAddr.Address = newKw;
                                if (getAddrOneNum != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrOneNum.Lng;
                                    newSpeechAddress.Lat_Y = getAddrOneNum.Lat;
                                    ShowAddr(0, getAddrOneNum.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                #endregion

                                return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.地址沒有號, 4).ConfigureAwait(false);
                            }
                        }
                    }
                }
                #endregion

                if (!string.IsNullOrEmpty(getAddr.City)) { thesaurusOne += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                if (!string.IsNullOrEmpty(getAddr.Road))
                {
                    if (getAddr.Road.Length > 4)
                    {
                        thesaurusOne += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                        thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                        thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                        thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                    }
                    else if (getAddr.Road.Length == 4)
                    {
                        thesaurusOne += " (FORMSOF(THESAURUS," + getAddr.Road + ") or FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ")) and";
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(getAddr.Road2))
                        {
                            thesaurusOne += " (FORMSOF(THESAURUS," + getAddr.Road + ") or FORMSOF(THESAURUS," + getAddr.Road2 + ")) and";
                        }
                        else
                        {
                            thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                        }
                    }
                }
                if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                if (!string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getAddr.Non))
                {
                    thesaurusOne += " (FORMSOF(THESAURUS," + getAddr.Lane + ") or";
                    thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Non + ")) and";
                }
                else
                {
                    if (!string.IsNullOrEmpty(getAddr.Lane))
                    {
                        if (getAddr.Lane.Length > 4)
                        {
                            thesaurusOne += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                            thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                            thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                            thesaurusOne += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                        }
                        else
                        {
                            thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                        }
                    }
                    if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                }
                if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusOne += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                if (string.IsNullOrEmpty(getAddr.Num) && addrRemoveOne.Length > 0)
                {
                    // 假如沒有號，把贅字加回來
                    thesaurusOne += " FORMSOF(THESAURUS," + addrRemoveOne + ") and";
                }
                if (!string.IsNullOrEmpty(thesaurusOne)) { thesaurusOne = thesaurusOne.Remove(thesaurusOne.Length - 3, 3).Trim(); }

                #region 有巷有弄有號 先用完整地判斷找一次
                if (!string.IsNullOrEmpty(getAddr.Num) && !string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getAddr.Non))
                {
                    var getAddr1 = await GoASRAPI(addrComb.Trim(), thesaurusOne.Length > 22 ? thesaurusOne : "", isNum: true, onlyAddr: getAddr.Lane + getAddr.Non + getAddr.Num).ConfigureAwait(false); ;
                    if (getAddr1 != null)
                    {
                        newSpeechAddress.Lng_X = getAddr1.Lng;
                        newSpeechAddress.Lat_Y = getAddr1.Lat;
                        ShowAddr(0, getAddr1.Address, newSpeechAddress, ref resAddr);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                }
                #endregion

                #endregion

                #region 有贅字且沒有號
                if (addrRemoveOne.Length > 0 && string.IsNullOrEmpty(getAddr.Num))
                {
                    _isNum = false;
                    _markName = addrRemoveOne;
                }
                #endregion

                #region 有號 但 沒城市沒區沒道路
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && string.IsNullOrEmpty(getAddr.Road) && string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.乘客問題).ConfigureAwait(false);
                }
                #endregion

                #region 有號 但 沒城市沒區有道路
                if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    // 檢查是否有重複的區
                    var tempKW = kw;
                    if (!string.IsNullOrEmpty(getAddr.Road)) { tempKW = tempKW.Replace(getAddr.Road, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Sect)) { tempKW = tempKW.Replace(getAddr.Sect, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Lane)) { tempKW = tempKW.Replace(getAddr.Lane, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Non)) { tempKW = tempKW.Replace(getAddr.Non, ""); }
                    if (!string.IsNullOrEmpty(getAddr.Num)) { tempKW = tempKW.Replace(getAddr.Num, ""); }
                    if (!string.IsNullOrEmpty(tempKW))
                    {
                        foreach (var d in getDistList)
                        {
                            if (tempKW.IndexOf(d) > -1 || (tempKW.IndexOf(d.Substring(0, 2)) > -1 && tempKW.IndexOf(d.Substring(0, 2) + "里") == -1))
                            {
                                getAddr.Dist = d;
                                break;
                            }
                        }
                    }
                }
                #endregion

                #region 有之號 先用完整重組的地址找一次
                if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.IndexOf("之") > -1)
                {
                    if (string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist))
                    {
                        var newAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        var getAddr2 = await GoASRAPI("", newAddr, onlyAddr: getAddr.Num, noCityDist: true).ConfigureAwait(false);
                        if (getAddr2 != null)
                        {
                            newSpeechAddress.Lng_X = getAddr2.Lng;
                            newSpeechAddress.Lat_Y = getAddr2.Lat;
                            ShowAddr(0, getAddr2.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        // 沒有City+Dist要先用去之號去查找道路
                        newAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num.Split("之")[0] + "號";
                        getAddr2 = await GoASRAPI("", newAddr, onlyAddr: newAddr, noCityDist: true).ConfigureAwait(false);
                        if (getAddr2 != null)
                        {
                            newSpeechAddress.Lng_X = getAddr2.Lng;
                            newSpeechAddress.Lat_Y = getAddr2.Lat;
                            ShowAddr(0, getAddr2.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    var newKw = "";
                    if (!string.IsNullOrEmpty(getAddr.City)) { newKw += getAddr.City; }
                    if (!string.IsNullOrEmpty(getAddr.Dist)) { newKw += getAddr.Dist; }
                    if (!string.IsNullOrEmpty(getAddr.Road)) { newKw += getAddr.Road; }
                    if (!string.IsNullOrEmpty(getAddr.Sect)) { newKw += getAddr.Sect; }
                    if (!string.IsNullOrEmpty(getAddr.Lane)) { newKw += getAddr.Lane; }
                    if (!string.IsNullOrEmpty(getAddr.Non)) { newKw += getAddr.Non; }
                    newKw += getAddr.Num;
                    var getAddr1 = await GoASRAPI(newKw, "", onlyAddr: getAddr.Num).ConfigureAwait(false);
                    resAddr.Address = newKw;
                    if (getAddr1 != null)
                    {
                        // 檢查號對不對
                        var getNum = SetGISAddress(new SearchGISAddress { Address = getAddr1.Address, IsCrossRoads = false });
                        if (getAddr.Num == getNum.Num)
                        {
                            var isNew = false;
                            newKw = getNum.City + getNum.Dist + getNum.Road + getNum.Sect + getNum.Lane + getNum.Non + getNum.Num;
                            if (string.IsNullOrEmpty(getAddr.Sect) && !string.IsNullOrEmpty(getNum.Sect)) { newKw = newKw.Replace(getNum.Sect, ""); isNew = true; }
                            if (string.IsNullOrEmpty(getAddr.Lane) && !string.IsNullOrEmpty(getNum.Lane)) { newKw = newKw.Replace(getNum.Lane, ""); isNew = true; }
                            if (string.IsNullOrEmpty(getAddr.Non) && !string.IsNullOrEmpty(getNum.Non)) { newKw = newKw.Replace(getNum.Non, ""); isNew = true; }
                            // 需要重找
                            if (isNew)
                            {
                                getAddr1 = await GoASRAPI(newKw, "").ConfigureAwait(false);
                                if (getAddr1 != null)
                                {
                                    newSpeechAddress.Lng_X = getAddr1.Lng;
                                    newSpeechAddress.Lat_Y = getAddr1.Lat;
                                    ShowAddr(0, getAddr1.Address, newSpeechAddress, ref resAddr, getAddr1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                            newSpeechAddress.Lng_X = getAddr1.Lng;
                            newSpeechAddress.Lat_Y = getAddr1.Lat;
                            ShowAddr(0, getAddr1.Address, newSpeechAddress, ref resAddr, getAddr1.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        else
                        {
                            // 先用找到的地址+原本的之號再找一次
                            var newKw1 = getAddr1.Address;
                            if (!string.IsNullOrEmpty(getNum.Num)) { newKw1 = getAddr1.Address.Replace(getNum.Num, getAddr.Num); }
                            getAddr1 = await GoASRAPI(newKw1, "").ConfigureAwait(false);
                            resAddr.Address = newKw1;
                            if (getAddr1 != null)
                            {
                                newSpeechAddress.Lng_X = getAddr1.Lng;
                                newSpeechAddress.Lat_Y = getAddr1.Lat;
                                ShowAddr(0, getAddr1.Address, newSpeechAddress, ref resAddr, getAddr1.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            // 拿掉之號再找一次
                            var newAddr = newKw.Replace(getAddr.Num, "") + getAddr.Num.Split("之")[0] + "號";
                            var getAddr2 = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                            resAddr.Address = newAddr;
                            if (getAddr2 != null)
                            {
                                newSpeechAddress.Lng_X = getAddr2.Lng;
                                newSpeechAddress.Lat_Y = getAddr2.Lat;
                                ShowAddr(0, getAddr2.Address, newSpeechAddress, ref resAddr, getAddr2.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        // 拿掉之號再找一次
                        var newAddr = "";
                        if (!string.IsNullOrEmpty(getAddr.City)) { newAddr += getAddr.City; }
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { newAddr += getAddr.Dist; }
                        if (!string.IsNullOrEmpty(getAddr.Road)) { newAddr += getAddr.Road; }
                        if (!string.IsNullOrEmpty(getAddr.Sect)) { newAddr += getAddr.Sect; }
                        if (!string.IsNullOrEmpty(getAddr.Lane)) { newAddr += getAddr.Lane; }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { newAddr += getAddr.Non; }
                        newAddr += getAddr.Num.Split("之")[0] + "號";
                        thesaurusOne = thesaurusOne.Replace(getAddr.Num, getAddr.Num.Split("之")[0] + "號");
                        var getAddr2 = await GoASRAPI(newAddr, thesaurusOne, isNum: _isNum).ConfigureAwait(false);
                        resAddr.Address = newAddr;
                        if (getAddr2 != null)
                        {
                            newSpeechAddress.Lng_X = getAddr2.Lng;
                            newSpeechAddress.Lat_Y = getAddr2.Lat;
                            ShowAddr(0, getAddr2.Address, newSpeechAddress, ref resAddr, getAddr2.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                #endregion

                #region 查看是否有"捷運站"關鍵字
                if (!string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.IndexOf("捷運站") > -1)
                {
                    var newAddr = getAddr.Road;
                    var arrKw = getAddr.Road.Split("捷運站");
                    if (arrKw.Length > 2 && !string.IsNullOrEmpty(arrKw[1]))
                    {
                        newAddr = "捷運" + arrKw[1] + "站";
                    }
                    else
                    {
                        newAddr = "捷運" + arrKw[0] + "站";
                    }
                    newAddr += getAddr.Num;
                    var getAddrMark = await GoASRAPI(newAddr, "", markName: newAddr).ConfigureAwait(false);
                    resAddr.Address = newAddr;
                    if (getAddrMark != null)
                    {
                        newSpeechAddress.Lng_X = getAddrMark.Lng;
                        newSpeechAddress.Lat_Y = getAddrMark.Lat;
                        ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    // 沒找到把出口拿掉再找一次
                    if (!string.IsNullOrEmpty(getAddr.Num))
                    {
                        var tempMarkName = newAddr.Replace(getAddr.Num, "");
                        getAddrMark = await GoASRAPI(tempMarkName, "", markName: tempMarkName).ConfigureAwait(false);
                        resAddr.Address = tempMarkName;
                        if (getAddrMark != null)
                        {
                            newSpeechAddress.Lng_X = getAddrMark.Lng;
                            newSpeechAddress.Lat_Y = getAddrMark.Lat;
                            ShowAddr(1, getAddrMark.Address, newSpeechAddress, ref resAddr, getAddrMark.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }

                #region 判斷捷運 比對最適配用
                if (!string.IsNullOrEmpty(addrComb) && addrComb.IndexOf("捷運") > -1 && addrComb.IndexOf("號") > -1 && addrComb.IndexOf("捷運站") == -1)
                {
                    foreach (var m in addrComb.Split("捷運"))
                    {
                        if (m.IndexOf("號") > -1)
                        {
                            _markName = m;
                        }
                    }
                }
                #endregion
                #endregion

                #region 檢查缺少道路基本單位
                if (!checkMarkName && string.IsNullOrEmpty(getAddr.City) && string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num) && getAddr.Road?.IndexOf("捷運站") == -1)
                {
                    var isRoad = false;
                    foreach (var s in crossRoadTwoWord)
                    {
                        if (kw.IndexOf(s) > -1)
                        {
                            isRoad = true;
                            break;
                        }
                    }
                    if (!isRoad)
                    {
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址).ConfigureAwait(false);
                    }
                }
                #endregion

                #region (2)重組地址與關鍵字 再查詢(找圖資)
                var _onlyAddr = !checkMarkName ? getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num : "";
                if ((!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    _onlyAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                }
                if ((string.IsNullOrEmpty(_onlyAddr) || (!string.IsNullOrEmpty(_onlyAddr) && _onlyAddr.IndexOf(getAddr.Num) > -1)) && !string.IsNullOrEmpty(getAddr.Num))
                {
                    _onlyAddr = "";
                    if (!string.IsNullOrEmpty(getAddr.Sect)) { _onlyAddr += getAddr.Sect; }
                    if (!string.IsNullOrEmpty(getAddr.Lane)) { _onlyAddr += getAddr.Lane; }
                    if (!string.IsNullOrEmpty(getAddr.Non)) { _onlyAddr += getAddr.Non; }
                    _onlyAddr += getAddr.Num;
                }
                if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Road2))
                {
                    _onlyAddr = _onlyAddr.Replace(getAddr.Road, getAddr.Road2);
                }
                var getAddrTow = await GoASRAPI(addrComb.Trim(), thesaurusOne.Length > 22 ? thesaurusOne : "", isNum: _isNum, markName: _markName, onlyAddr: _onlyAddr).ConfigureAwait(false);
                resAddr.Address = addrComb.Trim();
                if (getAddrTow != null && string.IsNullOrEmpty(getAddr.Lane) && string.IsNullOrEmpty(getAddr.Non))
                {
                    var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = getAddrTow.Address, IsCrossRoads = false });
                    if (!string.IsNullOrEmpty(getOtherAddr.Lane) || !string.IsNullOrEmpty(getOtherAddr.Non))
                    {
                        var newAddr = getOtherAddr.City + getOtherAddr.Dist + getOtherAddr.Road + getOtherAddr.Sect + getOtherAddr.Num;
                        var _getAddrTow = await GoASRAPI(newAddr, "", isNum: true, onlyAddr: newAddr).ConfigureAwait(false);
                        if (_getAddrTow != null)
                        {
                            newSpeechAddress.Lng_X = _getAddrTow.Lng;
                            newSpeechAddress.Lat_Y = _getAddrTow.Lat;
                            ShowAddr(0, _getAddrTow.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                }
                if (getAddrTow == null)
                {
                    if (_isNum.HasValue)
                    {
                        #region 判斷是否為部分地址+特殊地標
                        string[] markNamelist = { "大學", "高中", "國中", "國小" };
                        var kwMarkName = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                        if (!string.IsNullOrEmpty(kwMarkName) && !string.IsNullOrEmpty(addrRemoveOne) && addrRemoveOne.Length >= 4 && markNamelist.Any(x => addrRemoveOne.IndexOf(x) > -1))
                        {
                            var _mark = "";
                            foreach (var m in markNamelist)
                            {
                                var idx = addrRemoveOne.IndexOf(m);
                                if (idx > 1 && addrRemoveOne.Length > 3)
                                {
                                    _mark = addrRemoveOne.Substring(idx - 2, 4);
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(_mark))
                            {
                                var _thesaurusOne = "FORMSOF(THESAURUS," + kwMarkName + ") and";
                                _thesaurusOne += " FORMSOF(THESAURUS," + _mark + ") ";
                                getAddrTow = await GoASRAPI("", _thesaurusOne, markName: _mark).ConfigureAwait(false);
                                if (getAddrTow != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrTow.Lng;
                                    newSpeechAddress.Lat_Y = getAddrTow.Lat;
                                    ShowAddr(1, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(addrRemoveOne) && addrRemoveOne.Length >= 4)
                        {
                            if (addrRemoveOne.IndexOf("車站") > -1 && addrRemoveOne.IndexOf("火車站") == -1)
                            {
                                addrRemoveOne = addrRemoveOne.Replace("車站", "火車站");
                            }
                            var _thesaurusOne = "(FORMSOF(THESAURUS," + getAddr.City + ") or FORMSOF(THESAURUS," + getAddr.Dist + "))";
                            _thesaurusOne += " and FORMSOF(THESAURUS," + addrRemoveOne + ") ";
                            getAddrTow = await GoASRAPI("", _thesaurusOne, markName: addrRemoveOne).ConfigureAwait(false);
                            if (getAddrTow != null)
                            {
                                newSpeechAddress.Lng_X = getAddrTow.Lng;
                                newSpeechAddress.Lat_Y = getAddrTow.Lat;
                                ShowAddr(1, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        #endregion

                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road) &&
                             (!string.IsNullOrEmpty(getAddr.Sect) || !string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getAddr.Non)))
                        {
                            kwMarkName = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + "1號";
                            getAddrTow = await GoASRAPI(kwMarkName, "", onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non).ConfigureAwait(false);
                            if (getAddrTow != null)
                            {
                                newSpeechAddress.Lng_X = getAddrTow.Lng;
                                newSpeechAddress.Lat_Y = getAddrTow.Lat;
                                ShowAddr(0, getAddrTow.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }

                        // 移除號後面的字再找一次
                        if ((kw.IndexOf("號") + 1) < kw.Length)
                        {
                            var tempAddr = kw.Remove(kw.IndexOf("號") + 1);
                            var getTempAddr = SetGISAddress(new SearchGISAddress { Address = tempAddr, IsCrossRoads = false });
                            kwMarkName = getTempAddr.City + getTempAddr.Dist + getTempAddr.Road + getTempAddr.Sect + getTempAddr.Lane + getTempAddr.Non + getTempAddr.Num;
                            getAddrTow = await GoASRAPI(kwMarkName, "", isAddOne: true).ConfigureAwait(false);
                            if (getAddrTow != null)
                            {
                                newSpeechAddress.Lng_X = getAddrTow.Lng;
                                newSpeechAddress.Lat_Y = getAddrTow.Lat;
                                ShowAddr(0, getAddrTow.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }

                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址).ConfigureAwait(false);
                    }

                    #region 如果有完整道路資訊且沒之號 用之*找最近的
                    if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.IndexOf("之") == -1 && (!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) && !string.IsNullOrEmpty(getAddr.Road) &&
                       (!string.IsNullOrEmpty(getAddr.Sect) || !string.IsNullOrEmpty(getAddr.Lane) || !string.IsNullOrEmpty(getAddr.Non)))
                    {
                        var kwName = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num.Replace("號", "") + "之1號";
                        getAddrTow = await GoASRAPI(kwName, kwName, isNum: true, onlyAddr: kwName).ConfigureAwait(false);
                        if (getAddrTow != null)
                        {
                            newSpeechAddress.Lng_X = getAddrTow.Lng;
                            newSpeechAddress.Lat_Y = getAddrTow.Lat;
                            ShowAddr(0, getAddrTow.Address, newSpeechAddress, ref resAddr);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    #endregion

                    #region 是否同時念了地址跟特殊地標
                    if (addrRemove.Length > 5 && !string.IsNullOrEmpty(_onlyAddr))
                    {
                        var tempAddrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non;
                        var tempThesaurus = "";
                        for (var i = 0; i < addrRemove.Length; i++)
                        {
                            if (i <= (addrRemove.Length - 4))
                            {
                                tempThesaurus += " FORMSOF(THESAURUS," + addrRemove.Substring(i, 4) + ") or";
                            }
                            else
                            {
                                break;
                            }
                        }
                        tempThesaurus = tempThesaurus.Remove(tempThesaurus.Length - 2, 2).Trim();
                        getAddrTow = await GoASRAPI(tempAddrComb, tempThesaurus).ConfigureAwait(false);
                        if (getAddrTow != null)
                        {
                            newSpeechAddress.Lng_X = getAddrTow.Lng;
                            newSpeechAddress.Lat_Y = getAddrTow.Lat;
                            ShowAddr(1, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    #endregion

                    #region 判斷是否是沒有出入口的捷運
                    if (!string.IsNullOrEmpty(addrComb) && addrComb.IndexOf("捷運") > -1 && addrComb.IndexOf("捷運路") == -1 && addrComb.IndexOf("號") > -1)
                    {
                        var tempMarkName = addrComb.Substring(0, addrComb.IndexOf("站") + 1);
                        getAddrTow = await GoASRAPI(tempMarkName, "", markName: tempMarkName).ConfigureAwait(false);
                        if (getAddrTow != null)
                        {
                            newSpeechAddress.Lng_X = getAddrTow.Lng;
                            newSpeechAddress.Lat_Y = getAddrTow.Lat;
                            ShowAddr(1, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                    }
                    #endregion

                    #region 判斷City為同時有縣跟市的資料
                    if ((!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist)) &&
                        !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                    {
                        var getCity = "";
                        foreach (var _city in allCityTwo)
                        {
                            if (getAddr.City?.IndexOf(_city) > -1 || getAddr.Dist?.IndexOf(_city) > -1)
                            {
                                getCity = _city;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(getCity))
                        {
                            if (getAddr.Road?.IndexOf(getCity) > -1)
                            {
                                getAddr.Road = getAddr.Road.TrimStart('市');
                                getAddr.Road = getAddr.Road.TrimStart('縣');
                                var getRoad = SetGISAddress(new SearchGISAddress { Address = getAddr.Road, IsCrossRoads = false });
                                getAddr.Road = getRoad.Road;
                            }
                            var newAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            getAddrTow = await GoASRAPI("", newAddr, onlyAddr: newAddr).ConfigureAwait(false);
                            resAddr.Address = newAddr;
                            if (getAddrTow != null && getAddrTow.City?.IndexOf(getCity) > -1)
                            {
                                newSpeechAddress.Lng_X = getAddrTow.Lng;
                                newSpeechAddress.Lat_Y = getAddrTow.Lat;
                                ShowAddr(0, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }
                    #endregion

                    var thesaurusTwo = "";
                    if (addrComb.Length != kw.Length)
                    {
                        addrRemoveTow = kw;
                        #region 找出已知地址外的贅字
                        if (!string.IsNullOrEmpty(getAddr.City)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.City, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Dist, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Road)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Road, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Sect)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Sect, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Lane)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Lane, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Non, ""); }
                        if (!string.IsNullOrEmpty(getAddr.Num)) { addrRemoveTow = addrRemoveTow.Replace(getAddr.Num, ""); }
                        #endregion

                        #region 組全文檢索字串 將地址與贅字拆開
                        if (!string.IsNullOrEmpty(getAddr.City)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Road)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Lane)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                        if (!string.IsNullOrEmpty(addrRemoveTow)) { thesaurusTwo = thesaurusTwo + " FORMSOF(THESAURUS," + addrRemoveTow + ") and"; }
                        if (!string.IsNullOrEmpty(thesaurusTwo)) { thesaurusTwo = thesaurusTwo.Remove(thesaurusTwo.Length - 3, 3).Trim(); }
                        #endregion
                        addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    }
                    else
                    {
                        #region 判斷是不是區放到路了
                        if (string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road))
                        {
                            var getDist = SetGISAddress(new SearchGISAddress { Address = getAddr.Road, IsCrossRoads = false });
                            if (!string.IsNullOrEmpty(getDist.Dist))
                            {
                                getAddr.Dist = getDist.Dist;
                                getAddr.Road = getDist.Road;
                            }
                        }
                        #endregion

                        #region 判斷是不是路放到段了
                        if (!string.IsNullOrEmpty(getAddr.Sect))
                        {
                            var getNewRoad = SetGISAddress(new SearchGISAddress { Address = getAddr.Sect, IsCrossRoads = false });
                            if (!string.IsNullOrEmpty(getNewRoad.Road))
                            {
                                getAddr.Road = getNewRoad.Road;
                                getAddr.Sect = getNewRoad.Sect;
                            }
                        }
                        #endregion

                        #region 判斷是不是有鄰/里/村在Road/Lane中
                        if (!string.IsNullOrEmpty(getAddr.Road))
                        {
                            foreach (var str in delAddrs)
                            {
                                var arrRoad = getAddr.Road.Split(str);
                                if (arrRoad.Length > 1)
                                {
                                    getAddr.Road = arrRoad[1];
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Lane))
                        {
                            foreach (var str in delAddrs)
                            {
                                var arrLane = getAddr.Lane.Split(str);
                                if (arrLane.Length > 1)
                                {
                                    getAddr.Lane = arrLane[1];
                                }
                            }
                        }
                        #endregion

                        #region 重組全文檢索字串 如果長度一樣到這邊還是沒找到就移除City或Dist去查(可能唸的City或Dist是錯的)
                        addrComb = "";
                        // 移除City
                        if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Road))
                        {
                            if (getAddr.Road.Length > 4)
                            {
                                thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                            }
                            else
                            {
                                thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Sect))
                        {
                            if (getAddr.Sect.Length > 5)
                            {
                                thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 3, 3) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 4, 4) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 5, 5) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect + ")) and";
                            }
                            else
                            {
                                thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Sect + ") and";
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Lane))
                        {
                            if (getAddr.Lane.Length > 4)
                            {
                                thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                                thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                            }
                            else
                            {
                                thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                            }
                        }
                        if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                        if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                        if (!string.IsNullOrEmpty(thesaurusTwo)) { thesaurusTwo = thesaurusTwo.Remove(thesaurusTwo.Length - 3, 3).Trim(); }
                        var getAddrFour = await GoASRAPI("", thesaurusTwo.Trim(), onlyAddr: getAddr.Num).ConfigureAwait(false);
                        resAddr.Address = thesaurusTwo.Trim();
                        if (getAddrFour != null)
                        {
                            if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Num) && getAddr.City != getAddrFour.City)
                            {
                                #region 移掉Dist再找一次
                                addrComb = "";
                                thesaurusTwo = "";
                                if (!string.IsNullOrEmpty(getAddr.City)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Road))
                                {
                                    if (getAddr.Road.Length > 4)
                                    {
                                        thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                                    }
                                    else
                                    {
                                        thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Sect))
                                {
                                    if (getAddr.Sect.Length > 5)
                                    {
                                        thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 3, 3) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 4, 4) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect.Substring(getAddr.Sect.Length - 5, 5) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Sect + ")) and";
                                    }
                                    else
                                    {
                                        thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Sect + ") and";
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Lane))
                                {
                                    if (getAddr.Lane.Length > 4)
                                    {
                                        thesaurusTwo += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                                        thesaurusTwo += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                                    }
                                    else
                                    {
                                        thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                                if (!string.IsNullOrEmpty(thesaurusTwo)) { thesaurusTwo = thesaurusTwo.Remove(thesaurusTwo.Length - 3, 3).Trim(); }
                                var getAddrFour1 = await GoASRAPI("", thesaurusTwo.Trim()).ConfigureAwait(false);
                                resAddr.Address = thesaurusTwo.Trim();
                                if (getAddrFour1 != null)
                                {
                                    if (!ConfirmInfo(kw, getAddrFour1.Address))
                                    {
                                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                                    }
                                    newSpeechAddress.Lng_X = getAddrFour1.Lng;
                                    newSpeechAddress.Lat_Y = getAddrFour1.Lat;
                                    ShowAddr(0, getAddrFour1.Address, newSpeechAddress, ref resAddr, getAddrFour1.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }

                                #endregion

                                #region 雖然City不對但Dist是對的就回覆
                                if (!string.IsNullOrEmpty(getAddr.Dist))
                                {
                                    var getDist = SetGISAddress(new SearchGISAddress { Address = getAddrFour.Address, IsCrossRoads = false });
                                    if (getAddr.Dist == getDist.Dist)
                                    {
                                        newSpeechAddress.Lng_X = getAddrFour.Lng;
                                        newSpeechAddress.Lat_Y = getAddrFour.Lat;
                                        ShowAddr(0, getAddrFour.Address, newSpeechAddress, ref resAddr, getAddrFour.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                #endregion

                                return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                            }
                            if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Num) && getAddr.City == getAddrFour.City)
                            {
                                var getAddrNum = SetGISAddress(new SearchGISAddress { Address = getAddrFour.Address, IsCrossRoads = false });
                                if (getAddrNum.Num != getAddr.Num)
                                {
                                    return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                                }
                            }
                            newSpeechAddress.Lng_X = getAddrFour.Lng;
                            newSpeechAddress.Lat_Y = getAddrFour.Lat;
                            ShowAddr(0, getAddrFour.Address, newSpeechAddress, ref resAddr, getAddrFour.Memo);
                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                        }
                        else
                        {
                            // 再移除Dist
                            thesaurusTwo = "";
                            if (!string.IsNullOrEmpty(getAddr.City)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Road)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Lane)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                            if (!string.IsNullOrEmpty(thesaurusTwo)) { thesaurusTwo = thesaurusTwo.Remove(thesaurusTwo.Length - 3, 3).Trim(); }
                        }
                        #endregion
                    }

                    #region 檢查是否把City放到Road裡面了
                    if (string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                    {
                        var _getCity = getAddr.Road.IndexOf("市");
                        var _getCounty = getAddr.Road.IndexOf("縣");
                        if (_getCity > -1 || _getCounty > -1)
                        {
                            if (_getCity > -1 && (_getCity - 2) >= 0)
                            {
                                getAddr.City = getAddr.Road.Substring(_getCity - 2, 3);
                                getAddr.Road = getAddr.Road[(_getCity + 1)..]; // = getAddr.Road.Substring(_getCity + 1);
                            }
                            if (_getCounty > -1 && (_getCounty - 2) >= 0)
                            {
                                getAddr.City = getAddr.Road.Substring(_getCounty - 2, 3);
                                getAddr.Road = getAddr.Road[(_getCounty + 1)..];
                            }
                            addrComb = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            thesaurusTwo = "";
                        }
                        else
                        {
                            // 檢查是否把Dist、Road放到Num裡了
                            var _getDist = getAddr.Num.IndexOf("區");
                            var _getRoad = getAddr.Num.IndexOf("路");
                            if (_getDist > -1 && _getRoad > -1)
                            {
                                var _dist = "";
                                thesaurusTwo = "";
                                if ((_getDist - 1) >= 0) { _dist += " FORMSOF(THESAURUS," + getAddr.Num.Substring(_getDist - 1, 2) + ") or"; }
                                if ((_getDist - 3) >= 0) { _dist += " FORMSOF(THESAURUS," + getAddr.Num.Substring(_getDist - 3, 4) + ") or"; }
                                if ((_getDist - 2) >= 0)
                                {
                                    _dist += " FORMSOF(THESAURUS," + getAddr.Num.Substring(_getDist - 2, 3) + ") or";
                                    var _road = getAddr.Num[(_getDist + 1)..]; // =Num.Substring(_getDist + 1)
                                    var _getNewAddr = SetGISAddress(new SearchGISAddress { Address = _road, IsCrossRoads = false });
                                    if (!string.IsNullOrEmpty(_getNewAddr.Road)) { getAddr.Road = _getNewAddr.Road; }
                                    if (!string.IsNullOrEmpty(_getNewAddr.Num)) { getAddr.Num = _getNewAddr.Num; }
                                }
                                if (!string.IsNullOrEmpty(_dist)) { thesaurusTwo += " (" + _dist.Remove(_dist.Length - 2, 2).Trim() + ") and"; }

                                if (!string.IsNullOrEmpty(getAddr.Road)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Road + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Lane)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Lane + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurusTwo += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                                if (!string.IsNullOrEmpty(thesaurusTwo)) { thesaurusTwo = thesaurusTwo.Remove(thesaurusTwo.Length - 3, 3).Trim(); }
                                addrComb = "";
                            }
                        }
                    }
                    #endregion

                    #region 如果檢索字串與上一次相同則改變查詢條件
                    if (thesaurusTwo.Trim() == thesaurusOne.Trim())
                    {
                        // 如果只有號 判斷是否除了號還有別的值或中文號chineseNum
                        if (!string.IsNullOrEmpty(getAddr.Num) && string.IsNullOrEmpty(addrComb.Replace(getAddr.Num, "")))
                        {
                            foreach (var other in chineseNum)
                            {
                                var arrKw = kw.Split(other);
                                if (arrKw.Length > 1 && !string.IsNullOrEmpty(arrKw[0]))
                                {
                                    var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = other, IsCrossRoads = false });
                                    //getAddr.Road = arrKw[0];
                                    getAddr.Num = getOtherAddr.Num;
                                    addrComb = getAddr.Road + getAddr.Num;
                                    break;
                                }
                            }
                        }

                        #region 如果有路有號 換字再找一次
                        if (!string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                        {
                            var _addrComb1 = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            var _addrComb = _addrComb1;
                            foreach (var m in keyWordReplace)
                            {
                                var arrM = m.Split("|");
                                _addrComb = _addrComb.Replace(arrM[0], arrM[1]);
                            }
                            if (_addrComb1 != _addrComb)
                            {
                                var _getAddr = await GoASRAPI(_addrComb.Trim(), "").ConfigureAwait(false);
                                resAddr.Address = _addrComb.Trim();
                                if (_getAddr != null)
                                {
                                    newSpeechAddress.Lng_X = _getAddr.Lng;
                                    newSpeechAddress.Lat_Y = _getAddr.Lat;
                                    ShowAddr(0, _getAddr.Address, newSpeechAddress, ref resAddr, _getAddr.Memo);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                            }
                        }
                        #endregion

                        #region 如果有City有Dist有路有號 移除Dist再找一次
                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road) && !string.IsNullOrEmpty(getAddr.Num))
                        {
                            var _addrComb1 = getAddr.City + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            var _getAddr = await GoASRAPI(_addrComb1.Trim(), "").ConfigureAwait(false);
                            resAddr.Address = _addrComb1.Trim();
                            if (_getAddr != null)
                            {
                                newSpeechAddress.Lng_X = _getAddr.Lng;
                                newSpeechAddress.Lat_Y = _getAddr.Lat;
                                ShowAddr(0, _getAddr.Address, newSpeechAddress, ref resAddr, _getAddr.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                        #endregion
                    }
                    #endregion

                    #region (3)移除贅字 拆開地址 再查詢(找圖資)
                    if (!string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.Length < 2)
                    {
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                    }
                    if (!noCityDist && !string.IsNullOrEmpty(addrComb))
                    {
                        var getOtherAddr = SetGISAddress(new SearchGISAddress { Address = addrComb, IsCrossRoads = false });
                        if (string.IsNullOrEmpty(getOtherAddr.City) && string.IsNullOrEmpty(getOtherAddr.Dist))
                        {
                            noCityDist = true;
                        }
                    }

                    var getAddrThree = await GoASRAPI(addrComb.Trim(), thesaurusTwo.Trim(), noCityDist: noCityDist, onlyAddr: getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num).ConfigureAwait(false);
                    resAddr.Address = !string.IsNullOrEmpty(addrComb) ? addrComb : getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                    if (getAddrThree != null)
                    {
                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                    }
                    else
                    {
                        // 如果有完整的行政區且號為1號 則移除號找一次
                        if (!string.IsNullOrEmpty(getAddr.City) && !string.IsNullOrEmpty(getAddr.Dist) &&
                             (!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)) && getAddr.Num == "1號")
                        {
                            getAddrThree = await GoASRAPI(getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non, "").ConfigureAwait(false);
                            resAddr.Address = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non;
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }

                        if ((!string.IsNullOrEmpty(getAddr.Road) || !string.IsNullOrEmpty(getAddr.Lane)) && !string.IsNullOrEmpty(getAddr.Num))
                        {
                            // 先拆解後地址再找一次
                            var _addr0 = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                            getAddrThree = await GoASRAPI(_addr0, "").ConfigureAwait(false);
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            getAddrThree = await GoASRAPI("", _addr0).ConfigureAwait(false);
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }

                            #region 找鄰近的號
                            var tempNum = "";
                            var thesaurus3 = "";
                            var strNum = getAddr.Num.Replace("號", "");
                            if (strNum.IndexOf("之") > -1) { strNum = strNum.Split("之")[0]; }
                            // 號-2找一次
                            var successNum = int.TryParse(strNum, out var _num);
                            if (successNum && _num > 3)
                            {
                                tempNum = (_num - 2).ToString() + "號";
                                var _addr = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + tempNum;
                                getAddrThree = await GoASRAPI(_addr, "").ConfigureAwait(false);
                                resAddr.Address = _addr;
                                if (getAddrThree != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrThree.Lng;
                                    newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                    ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                getAddrThree = await GoASRAPI("", _addr).ConfigureAwait(false);
                                resAddr.Address = _addr;
                                if (getAddrThree != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrThree.Lng;
                                    newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                    ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                #region 改用全文檢索查
                                thesaurus3 = "";
                                if (!string.IsNullOrEmpty(getAddr.City)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Road))
                                {
                                    if (getAddr.Road.Length > 4)
                                    {
                                        thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                                    }
                                    else
                                    {
                                        thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                if (!string.IsNullOrEmpty(getAddr.Lane))
                                {
                                    if (getAddr.Lane.Length > 4)
                                    {
                                        thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                                        thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                                    }
                                    else
                                    {
                                        thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                                    }
                                }
                                if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                thesaurus3 += " FORMSOF(THESAURUS," + tempNum + ")";
                                getAddrThree = await GoASRAPI("", thesaurus3).ConfigureAwait(false);
                                if (getAddrThree != null)
                                {
                                    newSpeechAddress.Lng_X = getAddrThree.Lng;
                                    newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                    ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                }
                                #endregion
                            }

                            // 號+2找一次
                            tempNum = (_num + 2).ToString() + "號";
                            var _addr1 = getAddr.City + getAddr.Dist + getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + tempNum;
                            getAddrThree = await GoASRAPI(_addr1, "").ConfigureAwait(false);
                            resAddr.Address = _addr1;
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            getAddrThree = await GoASRAPI("", _addr1).ConfigureAwait(false);
                            resAddr.Address = _addr1;
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            #region 改用全文檢索查
                            thesaurus3 = "";
                            if (!string.IsNullOrEmpty(getAddr.City)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Road))
                            {
                                if (getAddr.Road.Length > 4)
                                {
                                    thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                                }
                                else
                                {
                                    thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                                }
                            }
                            if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                            if (!string.IsNullOrEmpty(getAddr.Lane))
                            {
                                if (getAddr.Lane.Length > 4)
                                {
                                    thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                                    thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                                }
                                else
                                {
                                    thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                                }
                            }
                            if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                            thesaurus3 += " FORMSOF(THESAURUS," + tempNum + ")";
                            getAddrThree = await GoASRAPI("", thesaurus3).ConfigureAwait(false);
                            if (getAddrThree != null)
                            {
                                newSpeechAddress.Lng_X = getAddrThree.Lng;
                                newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            #endregion
                            #endregion

                            // 移除可能的贅字
                            if (!string.IsNullOrEmpty(getAddr.Dist) && !string.IsNullOrEmpty(getAddr.Road) && getAddr.Road.Length > 4)
                            {
                                getAddr.Road = getAddr.Road.Replace(getAddr.Dist.Substring(0, 2), "");
                            }

                            // 如果有路有號 改不用SP查
                            var newAddr = "";
                            if (!string.IsNullOrEmpty(getAddr.City)) { newAddr += getAddr.City; }
                            if (!string.IsNullOrEmpty(getAddr.Dist)) { newAddr += getAddr.Dist; }
                            if (!string.IsNullOrEmpty(getAddr.Road)) { newAddr += getAddr.Road; }
                            if (!string.IsNullOrEmpty(getAddr.Sect)) { newAddr += getAddr.Sect; }
                            if (!string.IsNullOrEmpty(getAddr.Lane)) { newAddr += getAddr.Lane; }
                            if (!string.IsNullOrEmpty(getAddr.Non)) { newAddr += getAddr.Non; }
                            if (!string.IsNullOrEmpty(getAddr.Num)) { newAddr += getAddr.Num; }
                            var getAddrNoSP = await GetFactGisObjs(getAddr).ConfigureAwait(false);
                            resAddr.Address = newAddr;
                            if (getAddrNoSP != null)
                            {
                                newSpeechAddress.Lng_X = getAddrNoSP.Lng_X;
                                newSpeechAddress.Lat_Y = getAddrNoSP.Lat_Y;
                                ShowAddr(0, getAddrNoSP.GeocodeObjAddress, newSpeechAddress, ref resAddr);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                            else
                            {
                                #region 如果有巷 將巷改成號找找
                                if (!string.IsNullOrEmpty(getAddr.Lane))
                                {
                                    newAddr = newAddr.Replace(getAddr.Lane, "").Replace(getAddr.Num, "");
                                    newAddr += getAddr.Lane.Replace("巷", "") + "號";
                                    getAddrThree = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                                    resAddr.Address = newAddr;
                                    if (getAddrThree != null)
                                    {
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Dist))
                                    {
                                        newAddr = newAddr.Replace(getAddr.Dist, "");
                                        getAddrThree = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                                        resAddr.Address = newAddr;
                                        if (getAddrThree != null)
                                        {
                                            newSpeechAddress.Lng_X = getAddrThree.Lng;
                                            newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                            ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                            return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                        }
                                    }
                                }
                                #endregion

                                #region 移除City/Dist再一次
                                if (!string.IsNullOrEmpty(getAddr.City) || !string.IsNullOrEmpty(getAddr.Dist))
                                {
                                    if (!string.IsNullOrEmpty(getAddr.City))
                                    {
                                        newAddr = newAddr.Replace(getAddr.City, "");
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Dist))
                                    {
                                        newAddr = newAddr.Replace(getAddr.Dist, "");
                                    }
                                    var tempOnlyAddr = getAddr.Road + getAddr.Sect + getAddr.Lane + getAddr.Non + getAddr.Num;
                                    getAddrThree = await GoASRAPI("", newAddr, onlyAddr: tempOnlyAddr).ConfigureAwait(false);
                                    resAddr.Address = newAddr;
                                    if (getAddrThree != null)
                                    {
                                        if (!ConfirmInfo(newAddr, getAddrThree.Address))
                                        {
                                            return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                                        }
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                #endregion

                                #region  單純用地址再查一次
                                if (!string.IsNullOrEmpty(newAddr))
                                {
                                    getAddr = SetGISAddress(new SearchGISAddress { Address = newAddr, IsCrossRoads = false });
                                    thesaurus3 = "";
                                    if (!string.IsNullOrEmpty(getAddr.City)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.City + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddr.Dist)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Dist + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddr.Road))
                                    {
                                        if (getAddr.Road.Length > 4)
                                        {
                                            thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 3, 3) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 4, 4) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road.Substring(getAddr.Road.Length - 5, 5) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Road + ")) and";
                                        }
                                        else
                                        {
                                            thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Road + ") and";
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Sect)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Sect + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddr.Lane))
                                    {
                                        if (getAddr.Lane.Length > 4)
                                        {
                                            thesaurus3 += " (FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 4, 4) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane.Substring(getAddr.Lane.Length - 5, 5) + ") or";
                                            thesaurus3 += "  FORMSOF(THESAURUS," + getAddr.Lane + ")) and";
                                        }
                                        else
                                        {
                                            thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Lane + ") and";
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Non)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Non + ") and"; }
                                    if (!string.IsNullOrEmpty(getAddr.Num)) { thesaurus3 += " FORMSOF(THESAURUS," + getAddr.Num + ") and"; }
                                    if (!string.IsNullOrEmpty(thesaurus3)) { thesaurus3 = thesaurus3.Remove(thesaurus3.Length - 3, 3).Trim(); }
                                    getAddrThree = await GoASRAPI("", thesaurus3).ConfigureAwait(false);
                                    if (getAddrThree != null)
                                    {
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }

                                    var kw3 = "";
                                    if (!string.IsNullOrEmpty(getAddr.City)) { kw3 += getAddr.City; }
                                    if (!string.IsNullOrEmpty(getAddr.Road))
                                    {
                                        if (getAddr.Road.Length > 4)
                                        {
                                            kw3 += getAddr.Road.Substring(getAddr.Road.Length - 3, 3);
                                        }
                                        else
                                        {
                                            kw3 += getAddr.Road;
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Sect)) { kw3 += getAddr.Sect; }
                                    if (!string.IsNullOrEmpty(getAddr.Lane))
                                    {
                                        if (getAddr.Lane.Length > 4)
                                        {
                                            kw3 += getAddr.Lane.Substring(getAddr.Lane.Length - 3, 3);
                                        }
                                        else
                                        {
                                            kw3 += getAddr.Lane;
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getAddr.Non)) { kw3 += getAddr.Non; }
                                    if (!string.IsNullOrEmpty(getAddr.Num)) { kw3 += getAddr.Num; }
                                    getAddrThree = await GoASRAPI(kw3, "").ConfigureAwait(false);
                                    resAddr.Address = kw3;
                                    if (getAddrThree != null)
                                    {
                                        // 因為有去掉City/Dist 所以要比對道路是否正確
                                        var getAddr3 = SetGISAddress(new SearchGISAddress { Address = getAddrThree.Address, IsCrossRoads = false });
                                        if ((!string.IsNullOrEmpty(getAddr.Road) && getAddr3.Road != getAddr.Road) ||
                                            (!string.IsNullOrEmpty(getAddr.Lane) && getAddr3.Lane != getAddr.Lane))
                                        {
                                            return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                                        }
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    getAddrThree = await GoASRAPI("", kw).ConfigureAwait(false);
                                    resAddr.Address = kw;
                                    if (getAddrThree != null)
                                    {
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                #endregion

                                #region 還是沒有找到就改用特殊地標去找
                                var arrNotNum = kw.Split("號");
                                if (arrNotNum.Length > 1 && !string.IsNullOrEmpty(arrNotNum[1]) && arrNotNum[1].Length > 3)
                                {
                                    getAddrThree = await GoASRAPI(arrNotNum[1], "").ConfigureAwait(false);
                                    if (getAddrThree != null)
                                    {
                                        newSpeechAddress.Lng_X = getAddrThree.Lng;
                                        newSpeechAddress.Lat_Y = getAddrThree.Lat;
                                        ShowAddr(0, getAddrThree.Address, newSpeechAddress, ref resAddr, getAddrThree.Memo);
                                        return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                                    }
                                }
                                #endregion
                            }
                        }
                        return await SpeechAddressResponse(resAddr, 500, CallReasonEnum.無法判定地址, 1).ConfigureAwait(false);
                    }
                    #endregion
                }
                else
                {
                    // 檢查是否有之號 有的話先檢查號是否相同
                    if (!string.IsNullOrEmpty(getAddr.Num) && getAddr.Num.IndexOf("之") > -1)
                    {
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = getAddrTow.Address, IsCrossRoads = false });
                        if (getAddr.Num != getNewAddr.Num && !string.IsNullOrEmpty(getNewAddr.Num))
                        {
                            // 拿掉之號再找一次
                            var newAddr = getAddrTow.Address.Replace(getNewAddr.Num, "") + getAddr.Num.Split("之")[0] + "號";
                            var getAddr2 = await GoASRAPI(newAddr, "").ConfigureAwait(false);
                            resAddr.Address = newAddr;
                            if (getAddr2 != null)
                            {
                                newSpeechAddress.Lng_X = getAddr2.Lng;
                                newSpeechAddress.Lat_Y = getAddr2.Lat;
                                ShowAddr(0, getAddr2.Address, newSpeechAddress, ref resAddr, getAddr2.Memo);
                                return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                            }
                        }
                    }

                    newSpeechAddress.Lng_X = getAddrTow.Lng;
                    newSpeechAddress.Lat_Y = getAddrTow.Lat;
                    ShowAddr(0, getAddrTow.Address, newSpeechAddress, ref resAddr, getAddrTow.Memo);
                    return await SpeechAddressResponse(resAddr).ConfigureAwait(false);
                }
                #endregion

                #endregion 門牌/地標end

                #endregion 找出地址&座標 end
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return await SpeechAddressResponse(resAddr, 999, CallReasonEnum.系統錯誤).ConfigureAwait(false);
            }
        }

        private async Task<BaseResponse<GetAddrRes>> SpeechAddressResponse(GetAddrRes resAddr, int statusCode = 200, CallReasonEnum callReason = 0, int addrType = 0)
        {
            if (statusCode != 200)
            {
                await CallReason(new EditSpeechAddress { AsrId = resAddr.AsrId, CRNo = resAddr.CRNo, CallReason = (int)callReason, Addr = resAddr.Address, AddrType = addrType }).ConfigureAwait(false);
                var res = this.GenerateResponse(resAddr, statusCode: statusCode, message: callReason.ToDescription());
                _logger.LogInformation($"[{LogID}]DoSpeechAddress Res：{res.serializeJson()}");
                return res;
            }
            else
            {
                var res = this.GenerateResponse(resAddr);
                _logger.LogInformation($"[{LogID}]DoSpeechAddress Res：{res.serializeJson()}");
                return res;
            }
        }

        /// <summary>
        /// 判斷取得地址是否正確
        /// </summary>
        /// <param name="oldAddr"></param>
        /// <param name="newAddr"></param>
        /// <returns></returns>
        private bool ConfirmInfo(string oldAddr, string newAddr)
        {
            var isOK = true;
            var oldGetAddr = SetGISAddress(new SearchGISAddress { Address = oldAddr, IsCrossRoads = false });
            var newGetAddr = SetGISAddress(new SearchGISAddress { Address = newAddr, IsCrossRoads = false });
            if (!string.IsNullOrEmpty(oldGetAddr.Num))
            {
                if ((oldGetAddr.City == newGetAddr.City || oldGetAddr.Dist == newGetAddr.Dist) && (oldGetAddr.Road == newGetAddr.Road || oldGetAddr.Lane == newGetAddr.Lane)
                             && oldGetAddr.Num != newGetAddr.Num)
                {
                    isOK = false;
                }
            }
            return isOK;
        }
        #endregion

        #region 語音辨識
        /// <summary>
        /// 語音轉文字 呼叫 Google Cloud API (將所有文字組成一個字串回覆)
        /// Google Cloud [speech to text] API要點 1.音檔轉 bytes 2.audio.content 要 Base64
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("CallCloud"), HttpPost]
        public BaseResponse<string> CallCloud(CallCloudReq data)
        {
            var methodName = "CallCloud";
            // var stopwatch = System.Diagnostics.Stopwatch.StartNew();    // 開始計時
            // _logger.LogInformation($"[{LogID}]{methodName} 取得CallCloudReq[{data.serializeJson()}]");
            try
            {
                var cloudContent = "";
                var getClouds = _config.GetSection("GoogleCloud").Get<GoogleCloudConfig>();
                var elkUrl = _config.GetSection("ElasticSearch").GetSection("Uri").Get<string>();
                bool apiModelDefault;
                string url;
                if (getClouds != null)
                {
                    apiModelDefault = getClouds.ApiModelDefault;
                    url = getClouds.CloudUrl + "?key=" + getClouds.CloudKey;
                }
                else
                {
                    return this.GenerateResponse("", statusCode: 500, message: "缺少Google Cloud參數");
                }

                using var client = new HttpClient();
                if (!string.IsNullOrEmpty(data.Url))
                {
                    var wc = new System.Net.WebClient();
                    var bytes = wc.DownloadData(data.Url);
                    cloudContent = Convert.ToBase64String(bytes, 0, bytes.Length);
                }
                if (!string.IsNullOrEmpty(data.File))
                {
                    if (!string.IsNullOrEmpty(elkUrl))
                    {
                        // Linux 容器使用 不建立虛擬目錄的取法：安裝SharpCifs元件
                        var arrFile = data.File.Split(@"\");
                        var username = _config.GetValue<string>("SmbAccount");
                        var password = _config.GetValue<string>("SmbPassword");
                        var smbFile = new SmbFile($"smb://{username}:{password}@{arrFile[2]}/{arrFile[3]}/{arrFile[4]}/{arrFile[5]}");
                        var readStream = smbFile.GetInputStream();
                        var memStream = new MemoryStream();
                        ((Stream)readStream).CopyTo(memStream);
                        readStream.Dispose();
                        var bytes = memStream.ToArray();
                        cloudContent = Convert.ToBase64String(bytes, 0, bytes.Length);
                    }
                    else
                    {
                        var bytes = System.IO.File.ReadAllBytes(data.File);
                        cloudContent = Convert.ToBase64String(bytes, 0, bytes.Length);
                    }
                }
                if (!string.IsNullOrEmpty(data.BytesToBase64String))
                {
                    cloudContent = data.BytesToBase64String;
                }
                if (data.Bytes != null && data.Bytes.Length > 0)
                {
                    cloudContent = Convert.ToBase64String(data.Bytes, 0, data.Bytes.Length);
                }
                if (!string.IsNullOrEmpty(cloudContent))
                {
                    var bodyContent = JsonConvert.SerializeObject(new GoogleCloud
                    {
                        audio = new CloudAudio { content = cloudContent },
                        config = new RecognitionConfig { model = apiModelDefault ? "default" : "command_and_search" }
                    });
                    var buffer = Encoding.UTF8.GetBytes(bodyContent);
                    var byteContent = new ByteArrayContent(buffer);
                    byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    var response = client.PostAsync(url, byteContent).Result;
                    var result = response.Content.ReadAsStringAsync().Result;
                    var respAPI = JsonConvert.DeserializeObject<GoogleCloudRes>(result);
                    if (respAPI.error != null)
                    {
                        _logger.LogInformation($"[{LogID}]{methodName} 取得 GoogleCloudRes [{respAPI.serializeJson()}]");
                        return this.GenerateResponse(respAPI.error.status, statusCode: respAPI.error.code, message: respAPI.error.message);
                    }
                    var getText = "";
                    if (respAPI.results != null)
                    {
                        foreach (var r in respAPI.results) // 若判斷有斷行Cloud會切成多組回覆
                        {
                            getText += r.alternatives.FirstOrDefault().transcript.Trim();
                        }
                        // stopwatch.Stop();  // 停止計時
                        // _logger.LogInformation($"[{LogID}]{methodName} 執行時間：{stopwatch.Elapsed}");
                        return this.GenerateResponse(getText.Replace("\"", "").Replace("。", ""));
                    }
                    else
                    {
                        _logger.LogInformation($"[{LogID}]{methodName} 無法取得語音資料");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse("", statusCode: 999, message: ex.Message);
            }
            // stopwatch.Stop();  // 停止計時
            //  _logger.LogInformation($"[{LogID}]{methodName} 執行時間(2)：{stopwatch.Elapsed}");
            return this.GenerateResponse("", statusCode: 404, message: "無法取得資料");
        }
        static async Task<string> ReadFileAsync(FileStream fileStream)
        {
            using var reader = new StreamReader(fileStream);
            var fileContent = await reader.ReadToEndAsync();
            return fileContent;
        }
        /// <summary>
        /// 語音轉文字 呼叫 Google Cloud API
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("CallCloudMultRes"), HttpPost]
        public BaseResponse<List<string>> CallCloudMultRes(CallCloudReq data)
        {
            var methodName = "CallCloudMultRes";
            var res = new List<string>();
            try
            {
                var cloudContent = "";
                var getClouds = _config.GetSection("GoogleCloud").Get<GoogleCloudConfig>();
                bool apiModelDefault;
                string url;
                if (getClouds != null)
                {
                    apiModelDefault = getClouds.ApiModelDefault;
                    url = getClouds.CloudUrl + "?key=" + getClouds.CloudKey;
                }
                else
                {
                    return this.GenerateResponse(res, statusCode: 500, message: "缺少Google Cloud參數");
                }
                using var client = new HttpClient();
                if (!string.IsNullOrEmpty(data.Url))
                {
                    var wc = new System.Net.WebClient();
                    var bytes = wc.DownloadData(data.Url);
                    cloudContent = Convert.ToBase64String(bytes, 0, bytes.Length);
                }
                if (!string.IsNullOrEmpty(data.File))
                {
                    var bytes = System.IO.File.ReadAllBytes(data.File);
                    cloudContent = Convert.ToBase64String(bytes, 0, bytes.Length);
                }
                if (!string.IsNullOrEmpty(data.BytesToBase64String))
                {
                    cloudContent = data.BytesToBase64String;
                }
                if (data.Bytes != null && data.Bytes.Length > 0)
                {
                    cloudContent = Convert.ToBase64String(data.Bytes, 0, data.Bytes.Length);
                }
                var bodyContent = JsonConvert.SerializeObject(new GoogleCloud
                {
                    audio = new CloudAudio { content = cloudContent },
                    config = new RecognitionConfig { model = apiModelDefault ? "default" : "command_and_search" }
                });
                var buffer = Encoding.UTF8.GetBytes(bodyContent);
                var byteContent = new ByteArrayContent(buffer);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var response = client.PostAsync(url, byteContent).Result;
                var result = response.Content.ReadAsStringAsync().Result;
                var respAPI = JsonConvert.DeserializeObject<GoogleCloudRes>(result);
                if (respAPI.error != null)
                {
                    return this.GenerateResponse(res, statusCode: respAPI.error.code, message: respAPI.error.message);
                }
                if (respAPI.results != null)
                {
                    foreach (var r in respAPI.results) // 若判斷有斷行Cloud會切成多組回覆
                    {
                        res.Add(r.alternatives.FirstOrDefault().transcript.Trim());
                    }
                    return this.GenerateResponse(res);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse(res, statusCode: 999, message: ex.Message);
            }
            return this.GenerateResponse(res, statusCode: 404, message: "無法取得資料");
        }
        #endregion

        #region 拆解地址&判斷
        /// <summary>
        /// 查交叉路口 (只到段)
        /// </summary>
        /// <param name="search"></param>
        /// <returns></returns>
        private async Task<ApiGetTWCrossRoads> GoASRAPIByCrossRoads(SearchGISAddress search)
        {
            var methodName = "GoASRAPIByCrossRoads";
            try
            {
                search.Deleted = false;
                search.limit = 50;
                search.order = "asc";
                search.ordername = "Id";

                if (!string.IsNullOrEmpty(search.Sect))
                {
                    search.Sect = search.Sect.Replace("段", "");
                }
                if (!string.IsNullOrEmpty(search.Sect2))
                {
                    search.Sect2 = search.Sect2.Replace("段", "");
                }

                var (datas, total) = await _gisService.GetTWCrossRoadssAsync(search).ConfigureAwait(false);
                if (datas != null && total > 0)
                {
                    var getDatas = AutoMapperHelper.doMapper<TWCrossRoads, ApiGetTWCrossRoads>(datas);
                    foreach (var cross in getDatas)
                    {
                        // 組地址
                        cross.Addr1 = cross.City1.Trim() + cross.Dist1.Trim() + (cross.Villa1?.Trim() ?? "") + (cross.Road1?.Trim() ?? "") + (!string.IsNullOrEmpty(cross.Sect1) ? cross.Sect1.Trim() + "段" : "") + (cross.Ham1?.Trim() ?? "") + (!string.IsNullOrEmpty(cross.Lane1) ? cross.Lane1.Trim() + "巷" : "") + (!string.IsNullOrEmpty(cross.Non1) ? cross.Non1.Trim() + "弄" : "");
                        cross.Addr2 = cross.City2.Trim() + cross.Dist2.Trim() + (cross.Villa2?.Trim() ?? "") + (cross.Road2?.Trim() ?? "") + (!string.IsNullOrEmpty(cross.Sect2) ? cross.Sect2.Trim() + "段" : "") + (cross.Ham2?.Trim() ?? "") + (!string.IsNullOrEmpty(cross.Lane2) ? cross.Lane2.Trim() + "巷" : "") + (!string.IsNullOrEmpty(cross.Non2) ? cross.Non2.Trim() + "弄" : "");
                    }
                    #region 找最符合的            
                    var getData1 = getDatas.FirstOrDefault(x => x.Road1 == search.Road && x.Road2 == search.Road2 && (string.IsNullOrEmpty(search.Sect) ? x.Sect1 == null : x.Sect1 == search.Sect) && (string.IsNullOrEmpty(search.Sect2) ? x.Sect2 == null : x.Sect2 == search.Sect2));
                    if (getData1 != null)
                    {
                        return getData1;
                    }
                    var getData1_1 = getDatas.FirstOrDefault(x => x.Road1 == search.Road && x.Road2 == search.Road2 && (string.IsNullOrEmpty(search.Sect) ? 1 == 1 : x.Sect1 == search.Sect) && (string.IsNullOrEmpty(search.Sect2) ? 1 == 1 : x.Sect2 == search.Sect2));
                    if (getData1_1 != null)
                    {
                        return getData1_1;
                    }
                    var getData2 = getDatas.FirstOrDefault(x => x.Road2 == search.Road && x.Road1 == search.Road2 && (string.IsNullOrEmpty(search.Sect) ? x.Sect2 == null : x.Sect2 == search.Sect) && (string.IsNullOrEmpty(search.Sect2) ? x.Sect1 == null : x.Sect1 == search.Sect2));
                    if (getData2 != null)
                    {
                        return getData2;
                    }
                    #endregion
                    // 沒有再排序回傳
                    return getDatas.OrderBy(x => x.Road1).OrderBy(x => x.Sect1).OrderBy(x => x.Lane1).OrderBy(x => x.Non1).FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return null;
        }

        /// <summary>
        /// 查門牌 & 特殊地標 by 關鍵字
        /// </summary>
        /// <param name="kw">關鍵字</param>
        /// <param name="addr">拆解地址</param>
        /// <param name="isNum">是否有號</param>
        /// <param name="markName">地標名稱</param>
        /// <param name="onlyAddr">純地址</param>
        /// <param name="isAddOne">是否為加1號的查詢</param>
        /// <param name="noCityDist">是否沒有City與Dist</param>
        /// <returns></returns>
        private async Task<GisGeocodeObj> GoASRAPI(string kw, string addr, bool? isNum = null, string markName = "", string onlyAddr = "", bool? isAddOne = null, bool? noCityDist = null)
        {
            var methodName = "GoASRAPI";
            try
            {
                // 任務量較高的城市
                string[] highCity = { "台北市", "新北市", "台中市", "桃園市", "高雄市", "台南市" };

                if (!string.IsNullOrEmpty(kw) || !string.IsNullOrEmpty(addr))
                {
                    var getDatas = await _gisService.GetFactGisObjListAsync(150, kw, addr).ConfigureAwait(false);
                    if (getDatas != null && getDatas.Count > 0)
                    {
                        var respData = getDatas.Where(x => !x.Address.Contains("ATM") && !x.Memo.Contains("換電站") && !x.Memo.Contains("YouBike") && !x.Memo.Contains("ATM")
                        && !x.Memo.Contains("按摩小棧") && !x.Memo.Contains("校友") && !x.Memo.Contains("系友") && !x.Address.Contains("附近") && !x.Address.Contains("量販內")
                        && !x.Address.Contains("對面") && (!x.Address.Contains("業區內") || !x.Address.Contains("園區內")) && !x.Address.Contains("出口")).OrderBy(x => x.Memo);

                        if (kw.IndexOf("停車場") == -1) { respData = respData.Where(x => !x.Memo.Contains("停車場")).OrderBy(x => x.Address); }
                        if (kw.IndexOf("捷運") > -1) { respData = respData.Where(x => !x.Memo.Contains("店")).OrderBy(x => x.Address); }
                        if (kw.IndexOf("車站路") == -1 && addr.IndexOf("車站路") == -1 && kw.IndexOf("車站街") == -1 && addr.IndexOf("車站街") == -1)
                        {
                            respData = respData.Where(x => !x.Address.Contains("車站路") && !x.Address.Contains("車站街")).OrderBy(x => x.Address);
                        }
                        if (string.IsNullOrEmpty(markName) || markName.IndexOf("-") == -1)
                        {
                            if (markName.IndexOf("店") > -1 || markName.IndexOf("館") > -1 || markName.IndexOf("購物") > -1)
                            {
                                respData = respData.Where(x => !x.Address.Contains("樓")).OrderBy(x => x.Address);
                            }
                        }
                        if (respData.ToList().Count == 0)
                        {
                            return null;
                        }
                        #region 找最符合的
                        var getNewAddr = SetGISAddress(new SearchGISAddress { Address = kw, IsCrossRoads = false });
                        if (!string.IsNullOrEmpty(markName) && string.IsNullOrEmpty(getNewAddr.City) && string.IsNullOrEmpty(getNewAddr.Dist))
                        {
                            noCityDist = true;
                        }
                        if (noCityDist.HasValue && noCityDist.Value && respData.Count() > 1)
                        {
                            if (!string.IsNullOrEmpty(markName))
                            {
                                var getMarkDatas = respData.Where(x => x.Memo == markName || x.Memo == markName.Replace("-", ""));
                                if (getMarkDatas != null && getMarkDatas.Count() > 0)
                                {
                                    if (getMarkDatas.Count() == 1) { return getMarkDatas.FirstOrDefault(); }
                                    // 如果有重複名字的地標就檢查City
                                    if (!string.IsNullOrEmpty(getNewAddr.City))
                                    {
                                        var getAddr = getMarkDatas.Where(x => x.City == getNewAddr.City).FirstOrDefault();
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                    foreach (var c in highCity)
                                    {
                                        var getAddr = getMarkDatas.Where(x => x.City == c).FirstOrDefault();
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                }
                                var getMarkData = respData.Where(x => x.Memo.Contains(markName)).FirstOrDefault();
                                if (getMarkData != null)
                                {
                                    return getMarkData;
                                }
                            }
                            // 沒有提供City與Dist需要依城市任務量優先順序去找最符合的地址
                            if (!string.IsNullOrEmpty(onlyAddr))
                            {
                                foreach (var c in highCity)
                                {
                                    var getAddr = respData.Where(x => x.City == c && x.Address.Contains(onlyAddr)).FirstOrDefault();
                                    if (getAddr != null)
                                    {
                                        return getAddr;
                                    }
                                }
                            }
                            foreach (var c in highCity)
                            {
                                var _getAddr = respData.Where(x => x.City == c);
                                if (_getAddr.Count() > 0)
                                {
                                    if (!string.IsNullOrEmpty(kw))
                                    {
                                        var getAddr = _getAddr.Where(x => x.Address.Contains(kw)).FirstOrDefault();
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(addr))
                                    {
                                        var getAddr = _getAddr.Where(x => x.Address.Contains(addr)).FirstOrDefault();
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(getNewAddr.Road) && getNewAddr.Road.Length > 5 && !string.IsNullOrEmpty(onlyAddr))
                                    {
                                        var getAddr = _getAddr.FirstOrDefault(x => x.Address.Contains(onlyAddr[2..]));
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                }
                            }
                            return null;
                        }
                        if (!string.IsNullOrEmpty(onlyAddr) && onlyAddr.IndexOf("之") == -1)
                        {
                            var getAddr = respData.Where(x => x.Address.Contains(onlyAddr) && !x.Address.Contains("之")).FirstOrDefault();
                            if (getAddr != null)
                            {
                                return getAddr;
                            }
                        }
                        if (!string.IsNullOrEmpty(onlyAddr))
                        {
                            var getAddr = respData.Where(x => x.Address.Contains(onlyAddr)).FirstOrDefault();
                            if (getAddr != null)
                            {
                                return getAddr;
                            }
                        }
                        if (isNum.HasValue && !isNum.Value)
                        {
                            // 沒有提供號 要檢查找到的資料為特殊地標才可以
                            var getMarkData = respData.Where(x => x.Memo.Contains(markName)).OrderBy(c => c.Memo.Length).FirstOrDefault();
                            if (getMarkData != null)
                            {
                                return getMarkData;
                            }
                            else
                            {
                                return null;
                            }
                        }
                        if (!string.IsNullOrEmpty(markName))
                        {
                            var getMemo = respData.Where(x => x.Memo.Contains(markName)).OrderBy(c => c.Memo.Length).FirstOrDefault();
                            if (getMemo != null)
                            {
                                return getMemo;
                            }
                            else
                            {
                                var getMarkAddr = SetGISAddress(new SearchGISAddress { Address = markName, IsCrossRoads = false });
                                // 如果沒有給城市
                                if (string.IsNullOrEmpty(getMarkAddr.City) && string.IsNullOrEmpty(getMarkAddr.Dist))
                                {
                                    foreach (var c in highCity)
                                    {
                                        var getAddr = respData.Where(x => x.City == c).OrderBy(c => c.Memo.Length).FirstOrDefault();
                                        if (getAddr != null)
                                        {
                                            return getAddr;
                                        }
                                    }
                                }
                                // 刪掉縣市單位再找一次
                                var _markName = markName;
                                if (!string.IsNullOrEmpty(getMarkAddr.City)) { _markName = _markName.Replace(getMarkAddr.City, ""); }
                                if (!string.IsNullOrEmpty(getMarkAddr.Dist)) { _markName = _markName.Replace(getMarkAddr.Dist, ""); }
                                getMemo = respData.Where(x => x.Memo.Contains(_markName)).OrderBy(c => c.Memo.Length).FirstOrDefault();
                                if (getMemo != null)
                                {
                                    return getMemo;
                                }
                                if (!string.IsNullOrEmpty(markName) && markName.IndexOf("捷運") > -1)
                                {
                                    getMemo = respData.Where(x => x.Memo.Contains("捷運")).OrderBy(c => c.Memo.Length).FirstOrDefault();
                                    if (getMemo != null)
                                    {
                                        return getMemo;
                                    }
                                }
                            }
                        }
                        if (isAddOne.HasValue && isAddOne.Value && !string.IsNullOrEmpty(getNewAddr.Num))
                        {
                            var contains = kw.Replace(getNewAddr.Num, "");
                            if (!string.IsNullOrEmpty(getNewAddr.City)) { contains = contains.Replace(getNewAddr.City, ""); }
                            if (!string.IsNullOrEmpty(getNewAddr.Dist)) { contains = contains.Replace(getNewAddr.Dist, ""); }
                            var getAddOneData = respData.Where(x => x.Memo == "" && x.Address.Contains(contains)).OrderBy(c => c.Address).FirstOrDefault();
                            if (getAddOneData != null)
                            {
                                return getAddOneData;
                            }
                        }
                        var getData = respData.Where(x => x.Address.Contains(kw)).FirstOrDefault();
                        if (getData != null)
                        {
                            if (!string.IsNullOrEmpty(onlyAddr) && string.IsNullOrEmpty(markName))
                            {
                                getData.Memo = "";
                            }
                            return getData;
                        }
                        if (!string.IsNullOrEmpty(getNewAddr.City))
                        {
                            if (!string.IsNullOrEmpty(onlyAddr))
                            {
                                var getAddr = respData.Where(x => x.Address.Contains(onlyAddr)).FirstOrDefault();
                                if (getAddr != null)
                                {
                                    return getAddr;
                                }
                            }
                            getData = respData.Where(x => x.Address.Contains(kw.Replace(getNewAddr.City, ""))).FirstOrDefault();
                            if (getData != null)
                            {
                                if (!string.IsNullOrEmpty(onlyAddr) && string.IsNullOrEmpty(markName))
                                {
                                    getData.Memo = "";
                                }
                                return getData;
                            }
                            if (!string.IsNullOrEmpty(getNewAddr.Dist))
                            {
                                getData = respData.Where(x => x.Address.Contains(kw.Replace(getNewAddr.City + getNewAddr.Dist, ""))).FirstOrDefault();
                                if (getData != null)
                                {
                                    if (!string.IsNullOrEmpty(onlyAddr) && string.IsNullOrEmpty(markName))
                                    {
                                        getData.Memo = "";
                                    }
                                    return getData;
                                }
                            }
                            if (!string.IsNullOrEmpty(getNewAddr.Num))
                            {
                                getData = respData.Where(x => x.Address.Contains(getNewAddr.Num)).FirstOrDefault();
                                if (getData != null)
                                {
                                    if (string.IsNullOrEmpty(markName))
                                    {
                                        getData.Memo = "";
                                    }
                                    return getData;
                                }
                            }
                        }
                        if (isAddOne.HasValue && isAddOne.Value && getData == null)
                        {
                            return null;
                        }
                        if (!string.IsNullOrEmpty(onlyAddr))
                        {
                            if (!string.IsNullOrEmpty(markName))
                            {
                                var getAddrMemo = respData.Where(x => x.Address.Contains(onlyAddr) && x.Memo == markName).FirstOrDefault();
                                if (getAddrMemo != null)
                                {
                                    return getAddrMemo;
                                }
                            }
                            var getAddr = respData.Where(x => x.Address.Contains(onlyAddr)).FirstOrDefault();
                            if (getAddr != null)
                            {
                                return getAddr;
                            }
                        }
                        if (kw.IndexOf("號") == -1)
                        {
                            // 特殊地標
                            return respData.OrderBy(c => c.Memo.Length).FirstOrDefault();
                        }
                        #endregion

                        return respData.FirstOrDefault();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return null;
        }

        /// <summary>
        /// 拆解地址資料
        /// </summary>
        /// <param name="search"></param>
        /// <returns></returns>
        private SearchGISAddress SetGISAddress(SearchGISAddress search)
        {
            var methodName = "SetGISAddress";
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 全轉半需要(因為繁體)

                // 拆解地址 (參考原GIS拆解原則:忽略村里鄰)
                // *號裡面的-要轉成之，巷裡面的之要轉成-
                if (!string.IsNullOrEmpty(search.Address))
                {
                    var arrAddress = search.Address.Split('/');
                    var address = arrAddress[0];

                    if (string.IsNullOrEmpty(address)) { return search; }

                    var addressSplit = AddressSpilt.AddressSplit(address, out var successSpilt);
                    search.successSpilt = successSpilt;
                    search.City = addressSplit.City;
                    search.Dist = addressSplit.District;
                    if (addressSplit.Road == "大道" && (search.Address.IndexOf("台灣大道") > -1 || search.Address.IndexOf("臺灣大道") > -1))
                    {
                        search.Road = "台灣大道";
                    }
                    else
                    {
                        search.Road = addressSplit.Road;
                    }
                    if (search.IsCrossRoads)
                    {
                        // 交叉路口只到段
                        search.Sect = !string.IsNullOrEmpty(addressSplit.Section) ? addressSplit.Section : "";
                    }
                    else
                    {
                        search.Sect = addressSplit.Section;
                        search.Lane = addressSplit.Lane.Replace("之", "-");
                        search.Non = addressSplit.Non;
                        search.Num = addressSplit.No;
                    }

                    if (search.doChineseNum && !string.IsNullOrEmpty(search.Num))
                    {
                        var chineseNumList = new Dictionary<string, long>() { { "一", 1 }, { "二", 2 }, { "三", 3 }, { "四", 4 }, { "五", 5 }, { "六", 6 }, { "七", 7 }, { "八", 8 }, { "九", 9 } };
                        foreach (var c in chineseNumList)
                        {
                            if (search.Num.IndexOf(c.Key) > -1) { search.Num = search.Num.Replace(c.Key, c.Value.ToString()); break; }
                        }
                        var pattern = @"[\u4e00-\u9fa5]";
                        search.Num = Regex.Replace(search.Num, pattern, "") + "號"; // 移除非數字
                    }

                    if (arrAddress.Length > 1)
                    {
                        // 第二條交叉路口地址
                        var address2 = arrAddress[1];
                        if (!string.IsNullOrEmpty(address2))
                        {
                            var address2Split = AddressSpilt.AddressSplit(address2, out var successSpilt2);
                            search.successSpilt = !search.successSpilt || successSpilt2;
                            search.City2 = address2Split.City;
                            search.Dist2 = address2Split.District;
                            if (address2Split.Road == "大道" && (search.Address.IndexOf("台灣大道") > -1 || search.Address.IndexOf("臺灣大道") > -1))
                            {
                                search.Road2 = "台灣大道";
                            }
                            else
                            {
                                search.Road2 = address2Split.Road;
                            }
                            search.Sect2 = !string.IsNullOrEmpty(address2Split.Section) ? address2Split.Section : "";
                        }
                    }

                    #region 移除多餘字
                    string[] delKeyWord = { "啊", "啦", "的", "這個", "那個" };
                    foreach (var str in delKeyWord)
                    {
                        if (!string.IsNullOrEmpty(search.City))
                        {
                            search.City = search.City.Replace(str, "");
                        }
                        if (!string.IsNullOrEmpty(search.Dist))
                        {
                            search.Dist = search.Dist.Replace(str, "");
                        }
                        if (!string.IsNullOrEmpty(search.Road))
                        {
                            search.Road = search.Road.Replace(str, "");
                        }
                        if (!string.IsNullOrEmpty(search.Num))
                        {
                            search.Num = search.Num.Replace(str, "");
                        }
                    }
                    #endregion
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return search;
        }

        /// <summary>
        /// 組IVR需要的地址格式
        /// </summary>
        /// <param name="type">地址類型(0-門牌/1-地標/2-交叉路口/3-巷口等、弄口等、街口等)</param>
        /// <param name="addr">地址</param>
        /// <param name="newSpAddr">存DB資料</param>
        /// <param name="resAddr">API回傳值</param>
        /// <param name="markName">地標</param>
        /// <param name="crossRoad">第2條路(交叉路口)</param>
        /// <returns>參考Models GetAddrRes</returns>
        private void ShowAddr(int type, string addr, SpeechAddress newSpAddr, ref GetAddrRes resAddr, string markName = "", string crossRoad = "")
        {
            if (type == 1 && string.IsNullOrEmpty(markName))
            {
                type = 0;
            }
            if (!string.IsNullOrEmpty(markName))
            {
                type = 1;
            }
            if (!resAddr.CheckMarkName)
            {
                type = 0;
            }

            var addressSplit = AddressSpilt.AddressSplit(addr, out _);
            var getCity = "";
            // 取得座標區域碼
            newSpAddr.Zone = _ivrService.GetZone(newSpAddr.Lng_X, newSpAddr.Lat_Y);

            /*
             * 備註：
             * 230911 IVR要求Address參數加特殊符號 為了語音宣告不會太快
             */
            if (type == 1)
            {
                #region 特殊地標
                /*
一般地址(含地標)：
N|孫小姐@孫小姐|1|台中市@東區@立德街@@@@29號||T|120.686400|24.134940|073268|台中市東區立德街29號|0|0|4782|
說明：
固定值(N)|姓名@稱謂|地址編號(1)|縣市@行政區@路名@段@巷@弄@號|永久訊息(地標)|系統別(T)|X座標|Y座標|區域碼|完整地址|車隊碼|群組碼|派遣規則碼(UNION_PILOT)
          */
                newSpAddr.AddrType = 2;
                getCity = addressSplit.City;
                newSpAddr.Address = addr + "，" + markName;
                resAddr.Address = addressSplit.FullNameForSpiltByASR + "@" + markName;
                resAddr.PositionAddr = "N|" + newSpAddr.CustName + "|1|" + addressSplit.FullNameForSpiltByASR + "|" + markName + "|T|" + newSpAddr.Lng_X + "|" + newSpAddr.Lat_Y + "|" + newSpAddr.Zone + "|" + addr + "，" + markName + "|0|0|" + newSpAddr.UnionPilot + "|";
                #endregion
            }
            else if (type == 2)
            {
                #region 交叉路口 (只到段)
                /*
交叉路口：
N|孫小姐@孫小姐|1|台中市#東區#立德街##台中市#東區#建成路#||T|120.686400 |24.134940 |073268|台中市東區立德街與台中市東區建成路交叉路口|0|0|4782|
說明：
固定值(N)|姓名@稱謂|地址編號(1)|縣市#行政區#路名#段#縣市#行政區#路名#段|永久訊息|系統別(T)|X座標|Y座標|區域碼|完整地址|車隊碼|群組碼|派遣規則碼(UNION_PILOT)
                 */
                newSpAddr.AddrType = 3;
                var addr2 = crossRoad;
                var addressSplit1 = addressSplit;
                var addressSplit2 = AddressSpilt.AddressSplit(crossRoad, out _);
                if (addressSplit1.City == addressSplit2.City && addressSplit1.District == addressSplit2.District)
                {
                    addr2 = crossRoad.Replace(addressSplit1.City + addressSplit1.District, "");
                }
                var remark = "(請與乘客聯絡確認交叉路口方向)";
                getCity = addressSplit1.City;
                newSpAddr.Address = addr + "與" + addr2 + "交叉路口";
                var road2 = "";
                var arr2 = addressSplit2.FullNameForSpiltByCrossRoad.Split("#");
                for (var i = 0; i < arr2.Length; i++)
                {
                    if (i > 1)
                    {
                        road2 += "#" + arr2[i];
                    }
                }
                resAddr.Address = addressSplit1.FullNameForSpiltByCrossRoad + "#與" + road2 + "#交叉路口";
                var strCrossRoad = addressSplit1.FullNameForSpiltByCrossRoad + "#" + addressSplit2.FullNameForSpiltByCrossRoad;
                resAddr.PositionAddr = "N|" + newSpAddr.CustName + "|1|" + strCrossRoad + "|" + remark + "|T|" + newSpAddr.Lng_X + "|" + newSpAddr.Lat_Y + "|" + newSpAddr.Zone + "|" + newSpAddr.Address + "|0|0|" + newSpAddr.UnionPilot + "|";
                #endregion
            }
            else if (type == 3)
            {
                #region 巷口等、弄口等、街口等
                /* 格式：新北市@蘆洲區@光復路@@30巷@@|新北市蘆洲區光復路30巷，巷口等(長興路50巷)
一般地址：
N|孫小姐@孫小姐|1|台中市@東區@立德街@@@@29號||T|120.686400|24.134940|073268|台中市東區立德街29號|0|0|4782|
說明：
固定值(N)|姓名@稱謂|地址編號(1)|縣市@行政區@路名@段@巷@弄@號|永久訊息|系統別(T)|X座標|Y座標|區域碼|完整地址|車隊碼|群組碼|派遣規則碼(UNION_PILOT)

                 *不可以只念到段
                 */
                newSpAddr.AddrType = 4;
                var remark = crossRoad + "口等(請與乘客聯絡確認巷口方向)";
                getCity = addressSplit.City;
                newSpAddr.Address = addressSplit.FullNameWithoutNum + "，" + crossRoad + "口等";
                resAddr.Address = addressSplit.FullNameForSpiltByASRNoNum + "@@" + crossRoad + "口等";
                resAddr.PositionAddr = "N|" + newSpAddr.CustName + "|1|" + addressSplit.FullNameForSpiltByASRNoNum + "|" + remark + "|T|" + newSpAddr.Lng_X + "|" + newSpAddr.Lat_Y + "|" + newSpAddr.Zone + "|" + addressSplit.FullNameWithoutNum + "，" + crossRoad + "口等" + "|0|0|" + newSpAddr.UnionPilot + "|";
                #endregion
            }
            else
            {
                #region 門牌 (type == 0)
                /*
一般地址(含地標)：
N|孫小姐@孫小姐|1|台中市@東區@立德街@@@@29號||T|120.686400|24.134940|073268|台中市東區立德街29號|0|0|4782|
說明：
固定值(N)|姓名@稱謂|地址編號(1)|縣市@行政區@路名@段@巷@弄@號|永久訊息|系統別(T)|X座標|Y座標|區域碼|完整地址|車隊碼|群組碼|派遣規則碼(UNION_PILOT)
                 */
                newSpAddr.AddrType = 1;
                getCity = addressSplit.City;
                newSpAddr.Address = addr;
                resAddr.Address = addressSplit.FullNameForSpiltByASR;
                resAddr.PositionAddr = "N|" + newSpAddr.CustName + "|1|" + addressSplit.FullNameForSpiltByASR + "||T|" + newSpAddr.Lng_X + "|" + newSpAddr.Lat_Y + "|" + newSpAddr.Zone + "|" + addr + "|0|0|" + newSpAddr.UnionPilot + "|";
                #endregion
            }

            // 更新資料
            newSpAddr.City = getCity;
            newSpAddr.PositionAddr = resAddr.PositionAddr;
            newSpAddr.ModifyDate = DateTime.Now;
            var updateColumns = new[] { nameof(SpeechAddress.City), nameof(SpeechAddress.Address), nameof(SpeechAddress.PositionAddr), nameof(SpeechAddress.Lng_X), nameof(SpeechAddress.Lat_Y), nameof(SpeechAddress.Zone), nameof(SpeechAddress.AddrType), nameof(SpeechAddress.ModifyDate) }; // 要異動的欄位
            var asrId = AsyncHelper.RunSync(() => _ivrService.UpdateSpeechAddressAsync(newSpAddr, updateColumns));
            if (asrId > 0)
            {
                resAddr.AsrId = asrId;
            }
        }

        /// <summary>
        /// 取得縣市 & 鄉鎮區
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task<List<string>> GetDists(DistRequest data)
        {
            var methodName = "GetDists";
            try
            {
                // 先從Redis取資料
                try
                {
                    var citys = await _redisCacheManager.GetByPatternAsync(RedisCacheType.ASRCityDist, "*.*").ConfigureAwait(false);
                    if (citys.Count() > 0)
                    {
                        if (string.IsNullOrEmpty(data.City))
                        {
                            return citys.Select(a => a.Value.ToString()).Distinct().ToList();
                        }
                        return citys.Where(x => x.Key.Contains(data.City + ".")).Select(a => a.Value.ToString()).ToList();
                    }
                }
                catch { }
                // 沒有再從DB拿
                var result = await _gisService.GetCityListAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(data.City))
                {
                    return result.Select(a => a.Dist).Distinct().ToList();
                }
                return result.Where(x => x.City == data.City).Select(a => a.Dist).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return new List<string>();
        }

        /// <summary>
        /// 取得縣市 by 鄉鎮區
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task<string> GetCityByDist(DistRequest data)
        {
            var methodName = "GetCityByDist";
            try
            {
                string[] repeatWords = { "東區", "北區", "西區", "南區", "大安區", "中山區", "中正區", "信義區" };
                foreach (var w in repeatWords)
                {
                    if (w == data.Dist) { return ""; } // 不同城市有相同的鄉鎮區名
                }
                // 先從Redis取資料
                try
                {
                    var citys = await _redisCacheManager.GetByPatternAsync(RedisCacheType.ASRCityDist, "*.*").ConfigureAwait(false);
                    if (citys.Count() > 0)
                    {
                        if (!string.IsNullOrEmpty(data.Dist))
                        {
                            var getData = citys.Where(x => x.Value.ToString() == data.Dist).FirstOrDefault();
                            if (getData != null)
                            {
                                return getData.Key.Replace("." + data.Dist, "");
                            }
                        }
                    }
                }
                catch { }
                // 沒有再從DB拿
                var result = await _gisService.GetCityListAsync();
                if (!string.IsNullOrEmpty(data.Dist))
                {
                    return result.Where(x => x.Dist == data.Dist).Select(c => c.City).FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return "";
        }

        /// <summary>
        /// 呼叫 ASR API：查門牌 & 特殊地標  (非SP)
        /// </summary>
        /// <param name="search"></param>
        /// <returns></returns>
        private async Task<FactGisObj> GetFactGisObjs(SearchGISAddress search)
        {
            var datas = await _gisService.GetFactGisObjsAsync(search).ConfigureAwait(false);
            if (datas.Count > 0)
            {
                // 任務量較高的城市
                string[] highCity = { "台北市", "新北市", "台中市", "桃園市", "高雄市", "台南市" };
                if (string.IsNullOrEmpty(search.City) && string.IsNullOrEmpty(search.Dist) && datas.Count > 1)
                {
                    foreach (var c in highCity)
                    {
                        var _getAddr = datas.Where(x => x.GeocodeObjCity == c);
                        if (_getAddr.Count() > 0)
                        {
                            var getAddr = _getAddr.Where(x => x.GeocodeObjAddress.Contains(search.Address)).FirstOrDefault();
                            if (getAddr != null)
                            {
                                return getAddr;
                            }
                            if (_getAddr != null)
                            {
                                return _getAddr.FirstOrDefault();
                            }
                        }
                    }
                }
                return datas.FirstOrDefault();
            }
            return null;
        }

        /// <summary>
        /// 判斷字串裡的字或數值是否都一樣
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static bool AreAllCharactersSame(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }
            var firstChar = input[0];
            for (var i = 1; i < input.Length; i++)
            {
                if (input[i] != firstChar)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 找出連續重複的字詞
        /// </summary>
        /// <param name="input"></param>
        /// <returns>(重複的字詞,出現次數)</returns>
        public static (string entities, int total) FindRepeatingWords(string input)
        {
            // 使用正規表達式找出重複出現的字詞
            var matches = Regex.Matches(input, @"(\p{IsCJKUnifiedIdeographs}+)(\s*\1)+");
            // 輸出找到的重複字詞和出現次數
            foreach (Match match in matches)
            {
                var repeatingWord = match.Groups[1].Value;
                var occurrences = match.Groups[2].Captures.Count + 1; // 捕獲組2的次數 + 1
                return (repeatingWord, occurrences);
            }
            return ("", 0);
        }
        #endregion

        #region IVR參數
        /// <summary>
        /// 取得IVR參數清單
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("GetConfigList"), HttpPost]
        public async Task<List<IVRConfigModel>> GetConfigList()
        {
            var methodName = "GetConfigList";
            try
            {
                // 先從Redis取資料
                try
                {
                    var ivrs = await _redisCacheManager.HashGetAllAsync(RedisCacheType.ASRSysConfigsEnums, "IVR").ConfigureAwait(false);
                    if (ivrs.Count() > 0)
                    {
                        return ivrs.Select(a => new IVRConfigModel
                        {
                            Key = a.Key,
                            Val = a.Value.ToString(),
                            Memo = ((IVRConfigEnum[])Enum.GetValues(typeof(IVRConfigEnum))).Where(x => x.ToString() == a.Key).FirstOrDefault().ToDescription() ?? ""
                        }).ToList();
                    }
                }
                catch { }
                // 沒有再從DB拿
                var datas = await _sysConfigsService.GetSysConfigsListAsync("Enums", "IVR").ConfigureAwait(false);
                return datas.Select(a => new IVRConfigModel { Key = a.cfgKey, Val = a.cfgVal, Memo = a.Memo }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return new List<IVRConfigModel>();
        }
        #endregion

        #region 進線紀錄
        /// <summary>
        /// 進線紀錄異動
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("InboundType"), HttpPost]
        public async Task<BaseResponse<bool>> InboundType(SearchInboundType data)
        {
            var methodName = "InboundType";
            _logger.LogInformation($"[{LogID}]{methodName} Req：{data.serializeJson()}");
            try
            {
                if (string.IsNullOrEmpty(data.CRNo) || string.IsNullOrEmpty(data.FinalState))
                {
                    return this.GenerateResponse(false, statusCode: 500, message: "缺少必要參數");
                }
                var nowTime = DateTime.Now;
                var inbound = AutoMapperHelper.doMapper<SearchInboundType, InboundType>(data);
                inbound.CreateDate = nowTime;
                inbound.ModifyDate = nowTime;

                var (isAdd, crNo) = await _ivrService.CreateOrUpdateInboundTypeAsync(inbound).ConfigureAwait(false);

                #region 進入ASR就掛斷的情況SpeechAddress需要記錄一筆
                if (!isAdd && data.FinalState == "GoogleASR-InHangUp")
                {
                    var _SpeechAddress = await _ivrService.GetSpeechAddressAsync(0, data.CRNo).ConfigureAwait(false);
                    if (_SpeechAddress == null)
                    {
                        var newSpeechAddress = new SpeechAddress
                        {
                            CRNo = data.CRNo,
                            CustPhone = data.CustPhone,
                            FleetType = data.FleetType,
                            Trank_ID = data.Trank_ID,
                            CustName = "",
                            SpeechPath = ""
                        };
                        await _ivrService.CreateSpeechAddressAsync(newSpeechAddress).ConfigureAwait(false);
                    }
                }
                #endregion

                #region 不是新增就要加一筆紀錄到 HANGUP_POINT
                if (!isAdd && (data.FinalState == "GoogleASR-InHangUp" || data.FinalState == "GoogleASR-Addr" || data.FinalState == "GoogleASR-Addr-UnConfirm"))
                {
                    var _HangupReason = "剛進線";
                    if (data.FinalState == "GoogleASR-Addr") { _HangupReason = "辨識地址"; }
                    if (data.FinalState == "GoogleASR-Addr-UnConfirm") { _HangupReason = "未確認地址"; }
                    /*
                     1.沒有找到資料取新id帶入
                     2.有找到資料用原本的Id
                     */
                    var hp = new IVR.Models.Entities.FCS.HANGUP_POINT { CallerId = data.CustPhone, CDATE = nowTime, HangupReason = _HangupReason };
                    var getHp = await _fcsService.GetHANGUP_POINTAsync(data.CustPhone).ConfigureAwait(false);
                    if (getHp != null)
                    {
                        hp.ID = getHp.ID;
                        var updateColumns = new[] { nameof(IVR.Models.Entities.FCS.HANGUP_POINT.CDATE), nameof(IVR.Models.Entities.FCS.HANGUP_POINT.HangupReason) }; // 要異動的欄位
                        await _fcsService.UpdateHANGUP_POINTAsync(hp, updateColumns).ConfigureAwait(false);
                    }
                    else
                    {
                        // 取得id
                        var seqID = await _ivrService.GetHangupPointNumberNextValueAsync().ConfigureAwait(false);
                        hp.ID = seqID;
                        await _fcsService.CreateHANGUP_POINTAsync(hp).ConfigureAwait(false);
                    }
                }
                #endregion

                if (!string.IsNullOrEmpty(crNo))
                {
                    _logger.LogInformation($"[{LogID}]{methodName} Res：{true}");
                    return this.GenerateResponse(true, message: "成功");
                }
                _logger.LogInformation($"[{LogID}]{methodName} Res：{false}");
                return this.GenerateResponse(false, statusCode: 500, message: "更新失敗");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse(false, statusCode: 999, message: ex.Message);
            }
        }

        /// <summary>
        /// 取得IVR進線紀錄異動清單(分頁)
        /// 對應後台功能名稱：IVR進線紀錄
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("GetInboundTypeListAsync"), HttpPost]
        public async Task<BaseResponse<PagingResModel<GetInboundTypeRes>>> GetInboundTypeListAsync(SearchInboundType data)
        {
            var methodName = "GetInboundTypeListAsync";
            var datas = new List<GetInboundTypeRes>();
            try
            {
                if (data.page < 1 || data.pageSize < 1 || (data.order.ToLower() != "asc" && data.order.ToLower() != "desc") ||
                   !CheckIfPropertyNameUtil.CheckIfPropertyName<InboundType>(data.ordername))
                {
                    return this.GenerateResponse(new PagingResModel<GetInboundTypeRes> { total = 0, rows = datas }, statusCode: 500, message: "參數錯誤");
                }

                var (entities, total) = await _ivrService.GetInboundTypesAsync(data).ConfigureAwait(false);
                if (entities.Count > 0)
                {
                    datas = AutoMapperHelper.doMapper<InboundType, GetInboundTypeRes>(entities);
                    return this.GenerateResponse(new PagingResModel<GetInboundTypeRes>
                    {
                        total = total,
                        rows = datas
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return this.GenerateResponse(new PagingResModel<GetInboundTypeRes> { total = 0, rows = datas }, statusCode: 404, message: "查無資料");
        }
        #endregion

        #region 語音辨識紀錄(轉真人+任務編號)
        /// <summary>
        /// 紀錄轉真人原因
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("CallReason"), HttpPost]
        public async Task<BaseResponse<bool>> CallReason(EditSpeechAddress data)
        {
            var methodName = "CallReason";
            _logger.LogInformation($"[{LogID}]{methodName} Req：{data.serializeJson()}");
            if (data.AsrId <= 0 && string.IsNullOrEmpty(data.CRNo))
            {
                return this.GenerateResponse(false, statusCode: 500, message: "缺少必要參數");
            }
            if (data.CallReason <= 0)
            {
                return this.GenerateResponse(false, statusCode: 500, message: "缺少必要參數");
            }
            try
            {
                var nowTime = DateTime.Now;
                var addr = AutoMapperHelper.doMapper<EditSpeechAddress, SpeechAddress>(data);
                addr.CreateDate = nowTime;
                addr.ModifyDate = nowTime;

                var sAddr = await _ivrService.GetSpeechAddressAsync(addr.AsrId, addr.CRNo).ConfigureAwait(false);
                if (sAddr == null)
                {
                    return this.GenerateResponse(false, statusCode: 404, message: "查無資料");
                }
                #region 將地址辨識失敗訊息送給派遣
                if (data.CallReason == (int)CallReasonEnum.無法判定地址 || data.CallReason == (int)CallReasonEnum.客戶判定地址有誤)
                {
                    var apiValue = data.CallReason == (int)CallReasonEnum.無法判定地址 ? data.Addr : sAddr.Address;
                    if (!string.IsNullOrEmpty(apiValue) && !string.IsNullOrEmpty(sAddr.CustPhone))
                    {
                        var tgdsApiUrl = _config.GetValue<string>("TGDSApiUrl");
                        var reportErrAddrApi = _config.GetValue<string>("ReportErrAddrApi");
                        if (!string.IsNullOrEmpty(tgdsApiUrl))
                        {
                            using var client = new HttpClient();
                            var url = tgdsApiUrl.TrimEnd('/') + "/" + reportErrAddrApi;
                            var content = JsonConvert.SerializeObject(new ReportErrAddrReq { Phone = sAddr.CustPhone, Value = apiValue });
                            _logger.LogInformation($"[{LogID}]{methodName} ReportErrAddr Req：{content.serializeJson()}");
                            var buffer = Encoding.UTF8.GetBytes(content);
                            var byteContent = new ByteArrayContent(buffer);
                            byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            var response = await client.PostAsync(url, byteContent).ConfigureAwait(false);
                            var result = await response.Content.ReadAsStringAsync();
                            _logger.LogInformation($"[{LogID}]{methodName} ReportErrAddr Res：{result}");
                        }
                        else
                        {
                            _logger.LogInformation($"[{LogID}]{methodName} ReportErrAddr：TGDS API無法連線!!");
                        }
                    }
                }
                #endregion
                addr.AsrId = sAddr.AsrId;
                var updateColumns = new[] { nameof(SpeechAddress.CallReason), nameof(SpeechAddress.AddrType), nameof(SpeechAddress.ModifyDate) }; // 要異動的欄位
                if (!string.IsNullOrEmpty(data.Addr))
                {
                    addr.Address = data.Addr;
                    var addressSplit = AddressSpilt.AddressSplit(data.Addr, out _);
                    if (!string.IsNullOrEmpty(data.Addr)) { addr.City = addressSplit.City; }
                    updateColumns = new[] { nameof(SpeechAddress.City), nameof(SpeechAddress.Address), nameof(SpeechAddress.CallReason), nameof(SpeechAddress.AddrType), nameof(SpeechAddress.ModifyDate) }; // 要異動的欄位
                }
                var aID = await _ivrService.UpdateSpeechAddressAsync(addr, updateColumns).ConfigureAwait(false);
                if (aID > 0)
                {
                    _logger.LogInformation($"[{LogID}]{methodName} Res：{true}");
                    return this.GenerateResponse(true, message: "成功");
                }
                _logger.LogInformation($"[{LogID}]{methodName} Res：{false}");
                return this.GenerateResponse(false, statusCode: 500, message: "更新失敗");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse(false, statusCode: 999, message: ex.Message);
            }
        }

        /// <summary>
        /// 紀錄任務編號
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("UpdateJobID"), HttpPost]
        public async Task<BaseResponse<bool>> UpdateJobID(EditSpeechAddress data)
        {
            var methodName = "UpdateJobID";
            _logger.LogInformation($"[{LogID}]{methodName} Req：{data.serializeJson()}");

            if (data.AsrId <= 0 && string.IsNullOrEmpty(data.CRNo))
            {
                return this.GenerateResponse(false, statusCode: 500, message: "缺少必要參數");
            }
            if (string.IsNullOrEmpty(data.JobID))
            {
                return this.GenerateResponse(false, statusCode: 500, message: "缺少必要參數");
            }
            try
            {
                var nowTime = DateTime.Now;
                var addr = AutoMapperHelper.doMapper<EditSpeechAddress, SpeechAddress>(data);
                addr.CreateDate = nowTime;
                addr.ModifyDate = nowTime;
                if (string.IsNullOrEmpty(addr.IVENo) || addr.IVENo.Length > 5)
                {
                    addr.IVENo = "";
                }
                var sAddr = await _ivrService.GetSpeechAddressAsync(addr.AsrId, addr.CRNo).ConfigureAwait(false);
                if (sAddr == null)
                {
                    return this.GenerateResponse(false, statusCode: 404, message: "查無資料");
                }
                addr.AsrId = sAddr.AsrId;
                var updateColumns = new[] { nameof(SpeechAddress.JobID), nameof(SpeechAddress.IVENo), nameof(SpeechAddress.ETA), nameof(SpeechAddress.PayNum), nameof(SpeechAddress.JobNum), nameof(SpeechAddress.ModifyDate) }; // 要異動的欄位
                var aID = await _ivrService.UpdateSpeechAddressAsync(addr, updateColumns).ConfigureAwait(false);
                if (aID > 0)
                {
                    _logger.LogInformation($"[{LogID}]{methodName} Res：{true}");
                    return this.GenerateResponse(true, message: "成功");
                }
                _logger.LogInformation($"[{LogID}]{methodName} Res：{false}");
                return this.GenerateResponse(false, statusCode: 500, message: "更新失敗");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse(false, statusCode: 999, message: ex.Message);
            }
        }

        /// <summary>
        /// 取得語音辨識紀錄清單(分頁)
        /// 對應後台功能名稱：進線查詢
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [Route("GetSpeechAddressListAsync"), HttpPost]
        public async Task<BaseResponse<PagingResModel<GetSpeechAddressRes>>> GetSpeechAddressListAsync(SearchSpeechAddress data)
        {
            var methodName = "GetSpeechAddressListAsync";
            var totalRecordCount = 0;
            try
            {
                if (data.page < 1 || data.pageSize < 1 || (data.order.ToLower() != "asc" && data.order.ToLower() != "desc") ||
                   !CheckIfPropertyNameUtil.CheckIfPropertyName<GetSpeechAddressRes>(data.ordername))
                {
                    return this.GenerateResponse(new PagingResModel<GetSpeechAddressRes> { total = 0, rows = new List<GetSpeechAddressRes>() }, statusCode: 500, message: "參數錯誤");
                }

                var paramters = new DynamicParameters();
                paramters.Add("pCRNo", string.IsNullOrEmpty(data.CRNo) ? null : data.CRNo, DbType.String, size: 30);
                paramters.Add("pJobStatus", data.JobStatus, DbType.Int32);
                paramters.Add("pFleetType", string.IsNullOrEmpty(data.FleetType) ? null : data.FleetType, DbType.AnsiString, size: 10);
                paramters.Add("pTrank_ID", string.IsNullOrEmpty(data.Trank_ID) ? null : data.Trank_ID, DbType.AnsiString, size: 5);
                paramters.Add("pCustPhone", string.IsNullOrEmpty(data.CustPhone) ? null : data.CustPhone, DbType.AnsiString, size: 20);
                paramters.Add("pCity", string.IsNullOrEmpty(data.City) ? null : data.City, DbType.String, size: 10);
                paramters.Add("pAddress", string.IsNullOrEmpty(data.Address) ? null : data.Address, DbType.String, size: 255);
                paramters.Add("pFinalState", string.IsNullOrEmpty(data.FinalState) ? null : data.FinalState, DbType.String, size: 30);
                paramters.Add("pCallReason", data.CallReason, DbType.Int32);
                paramters.Add("pJobID", string.IsNullOrEmpty(data.JobID) ? null : data.JobID, DbType.AnsiString, size: 15);
                paramters.Add("pStartDate", data.StartDate, DbType.DateTime);
                paramters.Add("pEndDate", data.EndDate, DbType.DateTime);
                paramters.Add("pUserId", data.UserId, DbType.Int64);
                paramters.Add("pAddrType", data.AddrType, DbType.Int32);
                paramters.Add("pOrderColumn", data.ordername, DbType.String, size: 50);
                paramters.Add("pOrderDESC", data.order, DbType.String, size: 10);
                paramters.Add("pPageIndex", data.page, DbType.Int32);
                paramters.Add("pPageSize", data.pageSize, DbType.Int32);
                paramters.Add("pTotalRecordCount", totalRecordCount, DbType.Int32, ParameterDirection.Output);

                var (entities, total) = await _ivrService.GetSpeechAddressListAsync(paramters).ConfigureAwait(false);
                totalRecordCount = total;
                if (entities.Count > 0)
                {
                    // 派車狀態
                    entities.ForEach(x => x.CarSuccess = (x.FinalState == InboundTypeEnum.完成派車.ToDescription() || x.FinalState == InboundTypeEnum.取消叫車.ToDescription()));

                    // 付款方式 如果沒有值，預設為現金
                    entities.ForEach(x => x.PayNum = (x.PayNum == null ? 3 : x.PayNum.Value));

                    return this.GenerateResponse(new PagingResModel<GetSpeechAddressRes>
                    {
                        total = totalRecordCount,
                        rows = entities
                    });
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return this.GenerateResponse(new PagingResModel<GetSpeechAddressRes> { total = 0, rows = new List<GetSpeechAddressRes>() }, statusCode: 404, message: "查無資料");
        }
        #endregion

        #region 測試 溝通 IVR 與 AI 之間的傳遞
        [Route("GetAI"), HttpPost]
        public async Task<BaseResponse<GetAIRes>> GetAI(GetAIReq data)
        {
            var methodName = "GetAI";
            _logger.LogInformation($"[{LogID}]{methodName} Req：{data.serializeJson()}");
            var res = new GetAIRes { CRNo = data.CRNo };
            var msg = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(data.Msg))
                {
                    msg.Add(data.Msg);
                }
                else
                {
                    // 語音轉文字
                    if (string.IsNullOrEmpty(data.Msg) && !string.IsNullOrEmpty(data.SpeechPath))
                    {
                        data.CallCloud = new CallCloudReq { File = data.SpeechPath };
                        var callCloud = CallCloudMultRes(data.CallCloud);
                        if (callCloud != null && callCloud.StatusCode == 200 && callCloud.Result.Count > 0)
                        {
                            msg.AddRange(callCloud.Result);
                        }
                        if (callCloud != null && callCloud.StatusCode == 999)
                        {
                            return this.GenerateResponse(res, statusCode: 999, message: "語音轉文字錯誤");
                        }
                    }
                }
                if (msg.Count == 0)
                {
                    return this.GenerateResponse(res, statusCode: 404, message: "無訊息可供處理");
                }
                // 將文字丟給AI
                var getAI = await GoAI(msg);
                if (!string.IsNullOrEmpty(getAI))
                {
                    res.AiAnswer = getAI.Replace("\n\n", "　"); // AI會回覆換行符號
                }
                return this.GenerateResponse(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
                return this.GenerateResponse(res, statusCode: 999, message: "系統錯誤");
            }
        }

        private async Task<string> GoAI(List<string> msg)
        {
            var methodName = "GoAI";
            try
            {
                if (msg.Count == 0 || string.IsNullOrEmpty(msg[0])) { return ""; }
                var getConfigs = _config.GetSection("AzureAI").Get<AzureAIConfig>();
                string url;
                var reWordCount = 25;
                if (getConfigs != null)
                {
                    url = getConfigs.ChatGPTUrl + "&api-key=" + getConfigs.ApiKey;
                    if (getConfigs.ReWordCount > 0)
                    {
                        reWordCount = getConfigs.ReWordCount;
                    }
                }
                else
                {
                    return "";
                }
                var datas = new List<ChatGPTMsg>();
                foreach (var m in msg)
                {
                    if (!string.IsNullOrEmpty(m))
                    {
                        datas.Add(new ChatGPTMsg { role = "user", content = m });
                    }
                }
                // 系統指定回覆格式
                datas.Add(new ChatGPTMsg { role = "user", content = "請使用繁體中文回覆" });
                datas.Add(new ChatGPTMsg { role = "user", content = "請縮短回覆字數在" + reWordCount.ToString() + "字內" });

                using var client = new HttpClient();
                var content = JsonConvert.SerializeObject(new ChatGPTReq { messages = datas });
                var buffer = Encoding.UTF8.GetBytes(content);
                var byteContent = new ByteArrayContent(buffer);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var response = await client.PostAsync(url, byteContent).ConfigureAwait(false);
                var result = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(result)) { return ""; }
                var respAPI = JsonConvert.DeserializeObject<ChatGPTRes>(result);
                if (respAPI != null && respAPI.choices.Count > 0 && respAPI.choices[0].message != null)
                {
                    return respAPI.choices[0].message.content;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{LogID}]{methodName}");
            }
            return "";
        }
        #endregion
    }
}

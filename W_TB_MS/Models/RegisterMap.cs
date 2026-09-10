using System.Collections.Generic;

namespace W_TB_jiankong.Models
{
    public enum RegisterAccess
    {
        ReadOnly,
        ReadWrite,
        WriteOnly
    }

    public sealed record BitDefinition(string Name, string ZeroText, string OneText);

    /// <summary>
    /// W系列热泵 Modbus 寄存器完整定义（V2.4协议）
    /// 温度精度0.1℃，传输值=实际值*10
    /// </summary>
    public static class RegisterMap
    {
        // ============================================================
        //  输入寄存器 (功能码0x04，只读)
        // ============================================================

        #region --- 版本信息段 30001~30099 ---
        public const ushort HW_VERSION_ADDR = 30001;       // 主控板硬件版本号 (2寄存器)
        public const ushort SW_VERSION_ADDR = 30003;       // 主控板软件版本号 (2寄存器)
        public const ushort PRODUCT_MODEL_ADDR = 30005;    // 产品型号 (4寄存器,字符串)
        public const ushort COMPRESSOR_FW_ADDR = 30009;    // 压缩机驱动板软件版本号 (2寄存器)
        public const ushort FAN_FW_ADDR = 30011;           // 风机驱动板软件版本号 (2寄存器)
        #endregion

        #region --- 故障报警状态段 30101~30199 ---
        public const ushort FAULT_REG1_ADDR = 30101;       // 故障报警寄存器1 (BIT0~BIT15)
        public const ushort FAULT_REG2_ADDR = 30102;       // 故障报警寄存器2 (BIT0~BIT15)
        public const ushort FAULT_REG3_ADDR = 30103;       // 故障报警寄存器3 (BIT0~BIT15)
        public const ushort FAULT_REG4_ADDR = 30104;       // 故障报警寄存器4 (BIT0~BIT15)
        public const ushort FAULT_REG5_ADDR = 30105;       // 故障报警寄存器5
        #endregion

        #region --- 工作状态段 30106~30199 ---
        public const ushort STATUS_WORD_ADDR = 30106;      // 开关机、工作模式、除霜请求等状态位
        public const ushort OTA_STATUS_ADDR = 30107;       // 热泵主机升级状态
        public const ushort AC_INLET_FAST_ADDR = 30108;    // 空调进水温度快速值
        public const ushort COMPRESSOR_FREQ_FAST_ADDR = 30109; // 压缩机频率快速值
        public const ushort WRITE_COUNTER_ADDR = 30110;    // 4G模块写保持寄存器累加数
        #endregion

        #region --- 温度相关段 30201~30299 ---
        public const ushort DEVICE_STATUS_ADDR = 30201;    // 设备运行状态位
        public const ushort EXHAUST_TEMP_ADDR = 30202;     // 排气温度
        public const ushort HEAT_RECOVERY_OUT_TEMP_ADDR = 30203;
        public const ushort MID_COIL_TEMP_ADDR = 30204;
        public const ushort OUT_COIL_TEMP_ADDR = 30205;
        public const ushort ECONOMIZER_IN_TEMP_ADDR = 30206;
        public const ushort ECONOMIZER_OUT_TEMP_ADDR = 30207;
        public const ushort SUCTION_TEMP_ADDR = 30208;
        public const ushort AC_INLET_TEMP_ADDR = 30209;
        public const ushort AC_OUTLET_TEMP_ADDR = 30210;
        public const ushort AMBIENT_TEMP_ADDR = 30211;
        public const ushort HW_INLET_TEMP_ADDR = 30212;
        public const ushort HW_OUTLET_TEMP_ADDR = 30213;
        public const ushort HOT_WATER_TEMP_ADDR = 30214;
        public const ushort LOW_PRESSURE_ADDR = 30215;
        public const ushort HIGH_PRESSURE_ADDR = 30216;
        public const ushort CUMULATIVE_POWER_ADDR = 30217; // 占2个寄存器，高16位在前
        public const ushort EXV1_STEPS_ADDR = 30219;
        public const ushort EXV2_STEPS_ADDR = 30220;
        public const ushort COMPRESSOR_FREQ_ADDR = 30221;
        public const ushort UPPER_FAN_SPEED_ADDR = 30222;
        public const ushort THREE_WAY_VALVE1_ADDR = 30223;
        public const ushort THREE_WAY_VALVE2_ADDR = 30224;
        public const ushort LOWER_FAN_SPEED_ADDR = 30225;
        public const ushort DIP_ADDRESS_STATUS_ADDR = 30229;
        public const ushort AC_VOLTAGE_ADDR = 30231;
        public const ushort AC_CURRENT_ADDR = 30232;
        public const ushort POWER_CONSUMPTION_ADDR = 30233;
        public const ushort COMPRESSOR_TARGET_FREQ_ADDR = 30234;
        public const ushort COMPRESSOR_RUN_TIME_ADDR = 30235; // 占2个寄存器，低16位在前
        public const ushort COMPRESSOR_STOP_TIME_ADDR = 30237; // 占2个寄存器，低16位在前
        public const ushort DC_VOLTAGE_ADDR = 30239;
        public const ushort COMPRESSOR_CURRENT_ADDR = 30240;
        public const ushort DC_FAN_CURRENT_ADDR = 30241;
        public const ushort COMPRESSOR_IPM_TEMP_ADDR = 30242;
        #endregion

        #region --- 本地故障记录段 30301~30399 ---
        public const ushort FAULT_HISTORY_START_ADDR = 30301;
        public const ushort FAULT_HISTORY_TOTAL_ADDR = 30302;
        public const ushort FAULT_HISTORY_READ_COUNT_ADDR = 30303;
        public const ushort FAULT_HISTORY_BATCH_COUNT_ADDR = 30304;
        public const ushort FAULT_HISTORY_RECORD1_ADDR = 30305; // 每条记录占5个寄存器
        public const ushort FAULT_HISTORY_RECORD10_ADDR = 30350;

        public static ushort GetFaultHistoryRecordAddress(int recordIndex)
        {
            if (recordIndex is < 1 or > 10)
                throw new ArgumentOutOfRangeException(nameof(recordIndex), "故障记录序号必须在1到10之间");
            return checked((ushort)(FAULT_HISTORY_RECORD1_ADDR + (recordIndex - 1) * 5));
        }
        #endregion

        #region --- 产测及本地OTA状态段 30401~30599 ---
        public const ushort BOARD_TEST_STEP_ADDR = 30401;
        public const ushort LOCAL_OTA_ALLOWED_ADDR = 30501;
        public const ushort LOCAL_OTA_PACKAGE_SIZE_ADDR = 30502; // 占2个寄存器
        public const ushort LOCAL_OTA_PACKET_INDEX_ADDR = 30504; // 占2个寄存器
        public const ushort LOCAL_OTA_STATUS_ADDR = 30506;
        #endregion

        #region --- 机组设备地址段 30601~30699 ---
        public const ushort DEVICE_ADDRESS_TABLE_START_ADDR = 30601;
        public const ushort DEVICE_ADDRESS_TABLE_END_ADDR = 30632;
        #endregion

        #region --- 风盘及地暖输入寄存器 ---
        public const ushort FAN_HW_VERSION_ADDR = 31001; // 占2个寄存器
        public const ushort FAN_SW_VERSION_ADDR = 31003; // 占2个寄存器
        public const ushort FAN_MODEL_ADDR = 31005;
        public const ushort FAN_FEATURE_SET_ADDR = 31006;
        public const ushort FLOOR_HEATING_HW_VERSION_ADDR = 32001; // 占2个寄存器
        public const ushort FLOOR_HEATING_SW_VERSION_ADDR = 32003; // 占2个寄存器
        public const ushort FLOOR_HEATING_FAULT_ADDR = 32005;
        #endregion

        #region --- MixPad只读段 33001~33099 ---
        public const ushort MIXPAD_SCAN_ADDR = 33001;      // 设备扫描地址 (0xAA55)
        public const ushort MIXPAD_CMD_FLAG_ADDR = 33100;  // 有无命令数据标识
        public const ushort MIXPAD_CMD_DATA_START_ADDR = 33101;
        public const ushort MIXPAD_CMD_DATA_END_ADDR = 33199;
        #endregion

        // ============================================================
        //  保持寄存器（401xx为只写命令，其余定义段按协议读写）
        // ============================================================

        public static RegisterAccess GetAccess(ushort address)
        {
            if (address is >= 40101 and <= 40199)
                return RegisterAccess.WriteOnly;
            if (address is >= 40001 and <= 49999)
                return RegisterAccess.ReadWrite;
            return RegisterAccess.ReadOnly;
        }

        #region --- 常用读写命令段 40001~40099 ---
        public const ushort ON_OFF_ADDR = 40001;           // 开关机: 0x0000=关, 0x0001=开
        public const ushort SILENT_SET_ADDR = 40002;       // 静音模式: 0=普通, 1=静音, 2=超级静音
        public const ushort ZERO_COLD_SWITCH_ADDR = 40003; // 零冷水开关(周期模式)
        public const ushort ZERO_COLD_DURATION_ADDR = 40004; // 零冷水开启时长(min)
        public const ushort ZERO_COLD_REMAIN_ADDR = 40005; // 零冷水剩余时长(min)
        public const ushort MANUAL_DEFROST_ADDR = 40006;   // 手动除霜
        public const ushort STERILIZE_ADDR = 40007;        // 热水杀菌开关
        public const ushort ANTIFREEZE_HEATER_ADDR = 40008;// 防冻电加热开关
        public const ushort HOT_WATER_EXTEND_VALVE_ADDR = 40009; // 热水增程阀开关(W180)
        public const ushort ZERO_COLD_JOG_ADDR = 40010;    // 零冷水点动开关
        public const ushort FORCE_STANDBY_ADDR = 40011;    // 主机强制待机(W180)
        public const ushort INDOOR_DEVICE_COUNT_ADDR = 40012; // 室内设备运行数量(W180)
        #endregion

        #region --- 常用只写命令段 40101~40199 ---
        public const ushort CLEAR_FAULT_ADDR = 40101;      // 手动解除故障报警
        public const ushort WEATHER_TEMP_ADDR = 40102;     // 同步天气温度(4G)
        public const ushort DEFROST_CTRL_ADDR = 40103;     // 除霜请求控制
        public const ushort COMM_INTERRUPT_FLAG_ADDR = 40105; // 通信中断超2小时标志(4G)
        public const ushort CLEAR_DEVICE_INFO_ADDR = 40106;// 清除设备信息/恢复出厂
        public const ushort SYNC_TIME_ADDR = 40107;        // 同步时间(4G, 4寄存器UNIX时间戳)
        public const ushort WEATHER_HUMIDITY_ADDR = 40111; // 同步天气湿度(4G)
        #endregion

        #region --- 服务密码读写段 40201~40299 ---
        public const ushort SET_WORK_MODE_ADDR = 40201;    // 工作模式: 1=制冷, 2=制热
        public const ushort HOT_WATER_ENABLE_ADDR = 40202; // 生活热水启用开关
        public const ushort ZERO_COLD_PUMP_SUPPORT_ADDR = 40203; // 零冷水泵支持状态
        public const ushort COOLING_IN_TEMP_SET_ADDR = 40204;  // 制冷进水温度设置
        public const ushort COOLING_OUT_TEMP_SET_ADDR = 40205; // 制冷出水温度设置
        public const ushort HEATING_IN_TEMP_SET_ADDR = 40206;  // 制热进水温度设置
        public const ushort HEATING_OUT_TEMP_SET_ADDR = 40207; // 制热出水温度设置
        public const ushort TEMP_CONTROL_MODE_ADDR = 40208;    // 进出水温度控制依据
        public const ushort CAPABILITY_CALC_PERIOD_ADDR = 40209; // 能力计算周期(s)
        public const ushort AC_STOP_TEMP_DIFF_ADDR = 40210;    // 空调停机温差
        public const ushort AC_START_TEMP_DIFF_ADDR = 40211;   // 空调启动温差
        public const ushort HOT_WATER_MODE_ADDR = 40212;       // 生活热水模式: 0=节能, 1=舒适
        public const ushort AC_PUMP_PRE_RUN_ADDR = 40214;      // 空调循环水泵预运行时间(s)
        public const ushort WINTER_ANTIFREEZE_TEMP_ADDR = 40215; // 冬季防冻温度
        public const ushort COOL_ANTIFREEZE_TEMP_ADDR = 40216;  // 制冷防冻温度
        public const ushort DEFROST_DFA_ADDR = 40217;           // 除霜检测A点
        public const ushort DEFROST_DFB_ADDR = 40218;           // 除霜检测B点
        public const ushort DEFROST_EXIT_TEMP_ADDR = 40219;     // 除霜退出温度
        public const ushort DEFROST_MAX_TIME_ADDR = 40220;      // 除霜最长运行时间(min)
        public const ushort DEFROST_INTERVAL_ADDR = 40221;      // 除霜间隔时间(min)
        public const ushort DEFROST_MAX_AMBIENT_ADDR = 40222;   // 允许除霜最高环温
        public const ushort STANDBY_PUMP_MODE_ADDR = 40223;     // 待机空调水泵运行方式
        public const ushort STANDBY_PUMP_INTERVAL_ADDR = 40224; // 待机水泵交替间隔(min)
        public const ushort STANDBY_PUMP_RUN_ADDR = 40225;      // 待机水泵交替运行时间(min)
        public const ushort COMPRESSOR_PREHEAT_ADDR = 40226;    // 压缩机预热功能启用
        public const ushort POWER_MEMORY_ADDR = 40227;          // 断电记忆功能启用
        public const ushort AC_PUMP_SWITCH_ADDR = 40228;        // 空调循环水泵开关
        public const ushort HW_PUMP_SWITCH_ADDR = 40229;        // 热水水泵开关
        public const ushort ZERO_COLD_PUMP_SWITCH_ADDR = 40230; // 零冷水水泵开关
        public const ushort ZERO_COLD_JOG_DURATION_ADDR = 40231; // 零冷水点动运行时长(min)
        public const ushort ZERO_COLD_PUMP_ON_TIME_ADDR = 40232; // 零冷水水泵开启时长(min)
        public const ushort ZERO_COLD_PUMP_OFF_TIME_ADDR = 40233; // 零冷水水泵关闭时长(min)
        public const ushort ZERO_COLD_HOUR_START_ADDR = 40235;   // 零冷水整点启动时长(s)
        public const ushort ZERO_COLD_TEMP_THRESHOLD_ADDR = 40236; // 零冷水泵启动温度
        public const ushort ZERO_COLD_TEMP_DURATION_ADDR = 40237; // 零冷水温度持续时间(s)
        public const ushort ZERO_COLD_TEMP_INVALID_ADDR = 40238;  // 零冷水不可用温度
        public const ushort AC_PUMP_DELAY_ADDR = 40239;           // 空调水泵延时打开时长(s)(W180)
        #endregion

        #region --- 工厂密码段 40301~40399 ---
        public const ushort HEATING_IN_COMPENSATION_ADDR = 40301; // 制热进水温度补偿值
        public const ushort HEATING_OUT_COMPENSATION_ADDR = 40302; // 制热出水温度补偿值
        public const ushort COOLING_IN_COMPENSATION_ADDR = 40303; // 制冷进水温度补偿值
        public const ushort COOLING_OUT_COMPENSATION_ADDR = 40304; // 制冷出水温度补偿值
        public const ushort COMP_MAX_FREQ_ADDR = 40305;           // 压缩机运行最高频率
        public const ushort COMP_MIN_FREQ_ADDR = 40306;           // 压缩机运行最低频率
        public const ushort FAN_MAX_SPEED_ADDR = 40307;           // 风机运行最高转速
        public const ushort TARGET_SUPERHEAT_ADDR = 40308;        // 目标过热度
        public const ushort EXV1_PERIOD_ADDR = 40309;             // 主路EXV控制周期(s)
        public const ushort EXV2_PERIOD_ADDR = 40310;             // 辅路EXV控制周期(s)
        public const ushort COOL_RESONANCE_FREQ1_ADDR = 40311;
        public const ushort COOL_RESONANCE_FREQ2_ADDR = 40312;
        public const ushort COOL_RESONANCE_FREQ3_ADDR = 40313;
        public const ushort COOL_RESONANCE_FREQ4_ADDR = 40314;
        public const ushort COOL_RESONANCE_FREQ5_ADDR = 40315;
        public const ushort HEAT_RESONANCE_FREQ1_ADDR = 40316;
        public const ushort HEAT_RESONANCE_FREQ2_ADDR = 40317;
        public const ushort HEAT_RESONANCE_FREQ3_ADDR = 40318;
        public const ushort HEAT_RESONANCE_FREQ4_ADDR = 40319;
        public const ushort HEAT_RESONANCE_FREQ5_ADDR = 40320;
        public const ushort MAX_HEATING_WATER_K1_ADDR = 40321;
        public const ushort MAX_HEATING_WATER_K2_ADDR = 40322;
        public const ushort MAX_HEATING_WATER_K3_ADDR = 40323;
        public const ushort MAX_HEATING_WATER_K4_ADDR = 40324;
        #endregion

        #region --- 产测段 40401~40499 ---
        public const ushort SINGLE_BOARD_TEST_ADDR = 40401;  // 是否进入单板产测
        public const ushort FACTORY_TEST_ADDR = 40402;       // 是否进入工厂产测模式
        #endregion

        #region --- 4G模块IMEI/激活段 40501~40599 ---
        public const ushort IMEI_ADDR = 40501;               // 唯一识别码IMEI号 (9寄存器)
        public const ushort OUTDOOR_ACTIVATE_ADDR = 40510;   // 室外机激活状态
        public const ushort ACTIVATION_MD5_START_ADDR = 40511;
        public const ushort ACTIVATION_MD5_END_ADDR = 40518;
        public const ushort UNIT_MODE_ADDR = 40519;          // 单机/并联模式 (9寄存器)
        #endregion

        #region --- 4G OTA段 40601~40699 ---
        public const ushort OTA_START_ADDR = 40601;          // 是否启动新的升级
        public const ushort OTA_TYPE_ADDR = 40602;           // 升级类型
        public const ushort OTA_VERSION_ADDR = 40603;        // 升级软件版本 (2寄存器)
        public const ushort OTA_FILE_SIZE_ADDR = 40605;      // 升级文件大小 (2寄存器)
        public const ushort OTA_PACKET_NUM_ADDR = 40607;     // 当前升级包序号 (2寄存器)
        public const ushort OTA_PACKET_LEN_ADDR = 40609;     // 当前升级包长度
        public const ushort OTA_PACKET_DATA_ADDR = 40610;    // 当前升级包数据 (64寄存器)
        public const ushort OTA_PACKET_CRC_ADDR = 40674;     // 当前升级包校验码
        #endregion

        #region --- 热水器段 40701~40799 ---
        public const ushort HEATER_OUT_TEMP_ADDR = 40701;    // 热水器出水温度
        public const ushort HEATER_CAPACITY_ADDR = 40702;    // 热水器热水量
        public const ushort HEATER_HEAT_TIME_ADDR = 40703;   // 热水器加热时间
        #endregion

        #region --- 累计运行时长段 40801~40899 ---
        public const ushort COMP_RUNTIME_ADDR = 40801;       // 压缩机累计运行时长 (2寄存器,小时)
        public const ushort CUSTOM_FLASH_DATA_START_ADDR = 40851;
        public const ushort CUSTOM_FLASH_DATA_END_ADDR = 40899;
        #endregion

        #region --- 风盘控制段 41001~41099 ---
        public const ushort FAN_SHUTDOWN_ADDR = 41001;       // 关机
        public const ushort FAN_VALVE_SWITCH_ADDR = 41002;   // 风盘阀开关
        public const ushort FAN_SPEED_SET_ADDR = 41003;      // 风盘风速设置(0~100无极)
        public const ushort FRESH_AIR_SPEED_ADDR = 41004;    // 新风风速(0~100)
        public const ushort ANION_SWITCH_ADDR = 41005;       // 负离子除菌
        public const ushort E_HEATER_SWITCH_ADDR = 41006;    // 电加热
        public const ushort FAN_3D_VERT_ADDR = 41007;        // 3D风口上下扫风
        public const ushort FAN_3D_HORIZ_ADDR = 41008;       // 3D风口左右扫风
        public const ushort FRESH_AIR_RUNTIME_ADDR = 41011;  // 新风运行时长
        public const ushort FAN_FAULT_CODE_ADDR = 41012;     // 风盘故障码
        public const ushort CO2_CONCENTRATION_ADDR = 41013;  // CO2浓度
        public const ushort PM25_CONCENTRATION_ADDR = 41014; // PM2.5浓度
        public const ushort FAN_TEMP_ADDR = 41015;           // 风盘温度
        public const ushort FAN_HUMIDITY_ADDR = 41016;       // 风盘湿度
        public const ushort FAN_SPEED_RESET_ADDR = 41017;
        #endregion

        #region --- 风盘设置段 41101~41199 ---
        public const ushort FAN_MODEL_SET_ADDR = 41101;      // 风盘型号设置
        public const ushort FRESH_AIR_MIN_SPEED_ADDR = 41102; // 新风电机最低转速
        public const ushort FRESH_AIR_MAX_SPEED_ADDR = 41103; // 新风电机最高转速
        public const ushort FRESH_AIR_FILTER_REMINDER_ADDR = 41104;
        public const ushort FAN_MOTOR_MIN_SPEED_ADDR = 41105; // 风盘电机最低转速
        public const ushort FAN_MOTOR_MAX_SPEED_ADDR = 41106; // 风盘电机最高转速
        public const ushort FAN_MOTOR_SPEED_RESET_ADDR = 41107;
        #endregion

        #region --- 风盘产测段 41201~41299 ---
        public const ushort FAN_DIP_TEST_ADDR = 41201;
        #endregion

        #region --- 地暖控制段 42001~42199 ---
        public const ushort HEATER_VALVE1_ADDR = 42001;      // 分水器阀门1开关
        public const ushort HEATER_VALVE2_ADDR = 42002;      // 分水器阀门2开关
        public const ushort HEATER_VALVE3_ADDR = 42003;      // 分水器阀门3开关
        public const ushort HEATER_VALVE4_ADDR = 42004;      // 分水器阀门4开关
        public const ushort HEATER_VALVE5_ADDR = 42005;      // 分水器阀门5开关
        public const ushort HEATER_VALVE6_ADDR = 42006;      // 分水器阀门6开关
        public const ushort HEATER_VALVE7_ADDR = 42007;      // 分水器阀门7开关
        public const ushort HEATER_VALVE8_ADDR = 42008;      // 分水器阀门8开关
        public const ushort HEATER_ALL_VALVE_OFF_ADDR = 42100; // 关闭所有分水器阀门
        #endregion

        #region --- MixPad网络管理段 43001~43099 ---
        public const ushort MIXPAD_ADDR_CHANGE_ADDR = 43001; // 底壳485地址修改通知
        public const ushort MIXPAD_ONLINE_STATUS_ADDR = 43002; // 底壳在线离线状态
        public const ushort FAN_ONLINE_STATUS_ADDR = 43003;  // 风盘在线离线状态
        public const ushort HEATER_ONLINE_STATUS_ADDR = 43004; // 地暖在线离线状态
        public const ushort HEATPUMP_ONLINE_STATUS_ADDR = 43005; // 热泵在线离线状态
        public const ushort HEATPUMP_ADDR_TABLE_ADDR = 43006; // 热泵485地址表
        #endregion

        // ============================================================
        //  外机-4G Sheet
        // ============================================================

        #region --- 风盘外机-4G参数模板 ---
        public const ushort FAN_4G_TEMPLATE_BASE_ADDR = 20000;
        public const ushort FAN_4G_VALVE_OFFSET = 0;
        public const ushort FAN_4G_SPEED_OFFSET = 1;
        public const ushort FAN_4G_FRESH_AIR_OFFSET = 2;
        public const ushort FAN_4G_ANION_OFFSET = 3;
        public const ushort FAN_4G_HEATER_OFFSET = 4;
        public const ushort FAN_4G_VERTICAL_SWING_OFFSET = 5;
        public const ushort FAN_4G_HORIZONTAL_SWING_OFFSET = 6;
        public const ushort FAN_4G_CO2_OFFSET = 7;
        public const ushort FAN_4G_PM25_OFFSET = 8;
        public const ushort FAN_4G_TEMPERATURE_OFFSET = 9;
        public const ushort FAN_4G_HUMIDITY_OFFSET = 10;
        public const ushort FAN_4G_MODEL_OFFSET = 11;

        public static ushort GetFan4GAddress(int fanIndex, ushort offset)
        {
            if (fanIndex is < 1 or > 15)
                throw new ArgumentOutOfRangeException(nameof(fanIndex), "风盘序号必须在1到15之间");
            if (offset > FAN_4G_MODEL_OFFSET)
                throw new ArgumentOutOfRangeException(nameof(offset), "风盘参数偏移必须在0到11之间");
            return checked((ushort)(FAN_4G_TEMPLATE_BASE_ADDR + 60 * fanIndex + offset));
        }
        #endregion

        #region --- 外机-4G在线状态 ---
        public const ushort MANIFOLD_STATUS_START_ADDR = 21001;
        public const ushort MANIFOLD_STATUS_END_ADDR = 21015;
        public const ushort MANIFOLD_ONLINE_STATUS_ADDR = 21016;
        public const ushort FAN_4G_ONLINE_STATUS_ADDR = 21017;

        public static ushort GetManifold4GStatusAddress(int manifoldIndex)
        {
            if (manifoldIndex is < 1 or > 15)
                throw new ArgumentOutOfRangeException(nameof(manifoldIndex), "分集水器序号必须在1到15之间");
            return checked((ushort)(MANIFOLD_STATUS_START_ADDR + manifoldIndex - 1));
        }
        #endregion

        // ============================================================
        //  位定义映射
        // ============================================================

        /// <summary>故障报警寄存器1 (30101) 的位定义</summary>
        public static readonly Dictionary<int, string> FaultReg1Bits = new()
        {
            [0] = "冬季防冻保护",
            [1] = "空调水流量不足",
            [2] = "热水流量不足",
            [3] = "空调循环水泵过载",
            [4] = "热水循环水泵过载",
            [5] = "热水系统电加热过载",
            [6] = "排气温度传感器故障",
            [7] = "热回收出口温度传感器故障",
            [8] = "中盘温度传感器故障",
            [9] = "出盘温度传感器故障",
            [10] = "经济器进口温度传感器故障",
            [11] = "经济器出口温度传感器故障",
            [12] = "吸气温度传感器故障",
            [13] = "空调进水温度传感器故障",
            [14] = "空调出水温度传感器故障",
            [15] = "环境温度传感器故障",
        };

        /// <summary>故障报警寄存器2 (30102) 的位定义</summary>
        public static readonly Dictionary<int, string> FaultReg2Bits = new()
        {
            [0] = "热水进水温度传感器故障",
            [1] = "热水出水温度传感器故障",
            [2] = "热水温度传感器故障",
            [3] = "高压传感器故障",
            [4] = "低压传感器故障",
            [5] = "高压开关故障",
            [6] = "运行进出水温差过大",
            [7] = "进出水温度传感器插反",
            [8] = "环境温度保护",
            [9] = "制冷防冻保护",
            [10] = "排气过热度保护",
            [11] = "高压故障",
            [12] = "低压故障",
            [13] = "制冷剂泄漏",
            [14] = "排气温度过高",
            [15] = "吸气温度过高",
        };

        /// <summary>故障报警寄存器3 (30103) 的位定义</summary>
        public static readonly Dictionary<int, string> FaultReg3Bits = new()
        {
            [0] = "吸气过热度过小",
            [1] = "排温异常",
            [2] = "压缩机驱动故障",
            [3] = "上风机驱动故障",
            [4] = "压缩机驱动通讯故障",
            [5] = "风机驱动通讯故障",
            [6] = "压缩机驱动输入电流过载",
            [7] = "压缩机驱动输出电流过载",
            [8] = "驱动板和主控板通信故障",
            [9] = "PFC驱动故障",
            [10] = "IPM过热保护",
            [11] = "压缩机电流过大保护",
            [12] = "交流电流过大保护",
            [13] = "交流电压过低保护",
            [14] = "交流电压过高保护",
            [15] = "直流母线过压",
        };

        /// <summary>故障报警寄存器4 (30104) 的位定义</summary>
        public static readonly Dictionary<int, string> FaultReg4Bits = new()
        {
            [0] = "直流母线欠压",
            [1] = "压缩机软过流",
            [2] = "压缩机硬过流",
            [3] = "压缩机缺相",
            [4] = "AD噪声过大/偏置错误",
            [5] = "压缩机堵转",
            [6] = "速度推定错误或控制异常",
            [7] = "PFC Fo",
            [8] = "热泵主机和4G LTE通讯故障",
            [9] = "热泵主机和电量模块通讯故障",
            [10] = "热泵主机和MixPad通讯故障",
            [11] = "机组电源异常",
            [12] = "生活热水防冻保护",
            [13] = "下风机驱动故障",
            [14] = "热水上温度传感器故障",
            [15] = "热水中温度传感器故障",
        };

        /// <summary>故障报警寄存器5 (30105) 的位定义</summary>
        public static readonly Dictionary<int, string> FaultReg5Bits = new()
        {
            [0] = "热水下温度传感器故障",
            [1] = "热水进水温度过低",
            [15] = "空调水流量不足（未锁定）",
        };

        /// <summary>主机运行状态（30106）位定义</summary>
        public static readonly Dictionary<int, BitDefinition> StatusWordBits = new()
        {
            [0] = new("热泵主机开关机状态", "关机", "开机"),
            [1] = new("零冷水点动开关状态", "关闭", "打开"),
            [2] = new("强制待机状态（W180）", "关闭", "打开"),
            [3] = new("热泵主机工作模式位0", "0", "1"),
            [4] = new("热泵主机工作模式位1", "0", "1"),
            [5] = new("热泵主机工作模式位2", "0", "1"),
            [6] = new("生活热水运行状态", "未运行", "运行"),
            [7] = new("生活热水水箱电加热状态", "关闭", "运行"),
            [8] = new("零冷水功能开关状态", "关闭", "运行"),
            [9] = new("热水器杀菌状态", "关闭", "运行"),
            [10] = new("热泵主机激活状态位0", "0", "1"),
            [11] = new("热泵主机激活状态位1", "0", "1"),
            [12] = new("热泵主机除霜请求", "无请求", "有请求"),
            [13] = new("防冻电加热状态（W180）", "关闭", "运行"),
            [14] = new("热水增程阀开关状态（W180）", "关闭", "运行"),
            [15] = new("空调水泵打开预处理（W180）", "关闭", "预处理")
        };

        /// <summary>设备运行状态（30201）位定义</summary>
        public static readonly Dictionary<int, BitDefinition> DeviceStatusBits = new()
        {
            [0] = new("空调水流量开关", "断开", "接通"),
            [1] = new("热水流量开关", "断开", "接通"),
            [2] = new("空调循环水泵", "断开", "接通"),
            [3] = new("热水循环水泵（100W）", "断开", "接通"),
            [4] = new("压缩机曲轴加热带（35W）", "断开", "接通"),
            [5] = new("四通阀", "断开", "接通"),
            [6] = new("底盘电加热（70W）", "断开", "接通"),
            [7] = new("零冷水水泵（100W）", "断开", "接通"),
            [8] = new("主路电子膨胀阀（EXV1）", "断开", "接通"),
            [9] = new("辅路电子膨胀阀（EXV2）", "断开", "接通"),
            [10] = new("电动三通球阀1", "断开", "接通"),
            [11] = new("电动三通球阀2", "断开", "接通"),
            [12] = new("除霜状态", "关闭", "运行"),
            [13] = new("水路防冻运行状态", "关闭", "运行"),
            [14] = new("生活热水水箱电加热状态", "关闭", "运行"),
            [15] = new("防冻电加热状态", "关闭", "运行")
        };

        /// <summary>K1拨码及阀门状态（30229）位定义，BIT11～BIT15为预留</summary>
        public static readonly Dictionary<int, BitDefinition> DipSwitchBits = new()
        {
            [0] = new("K1拨码第1位（热泵地址高位）", "OFF", "ON"),
            [1] = new("K1拨码第2位（热泵地址低位）", "OFF", "ON"),
            [2] = new("K1拨码第3位（扩展水箱）", "支持", "不支持"),
            [3] = new("K1拨码第4位（热水器）", "支持", "不支持"),
            [4] = new("K1拨码第5位", "OFF", "ON"),
            [5] = new("K1拨码第6位", "OFF", "ON"),
            [6] = new("K1拨码第7位", "OFF", "ON"),
            [7] = new("K1拨码第8位", "OFF", "ON"),
            [8] = new("水路电动二通阀（热水增程阀）", "断开", "接通"),
            [9] = new("旁通阀（W260、W300）", "断开", "接通"),
            [10] = new("回油电磁阀状态（W180）", "断开", "接通")
        };

        /// <summary>风盘故障码 (41012) 的位定义</summary>
        public static readonly Dictionary<int, string> FanFaultBits = new()
        {
            [0] = "风盘485地址拨码错误",
            [1] = "风盘型号拨码错误",
            [2] = "风盘与底壳485通信故障",
            [3] = "风盘电机1故障",
            [4] = "风盘电机2故障",
            [5] = "新风电机故障",
            [6] = "CO2传感器故障",
            [7] = "温湿度传感器故障",
            [8] = "PM2.5传感器故障",
            [9] = "排水故障",
        };

        /// <summary>热泵主机工作模式映射</summary>
        public static readonly Dictionary<ushort, string> WorkModeMap = new()
        {
            [0] = "制冷",
            [1] = "制冷待机",
            [2] = "制冷报警停机",
            [3] = "制热",
            [4] = "制热待机",
            [5] = "制热报警停机",
            [6] = "除霜中",
            [7] = "压缩机预热",
        };

        /// <summary>静音模式映射</summary>
        public static readonly Dictionary<ushort, string> SilentModeMap = new()
        {
            [0] = "普通",
            [1] = "静音",
            [2] = "超级静音",
        };

        /// <summary>热泵主机故障码映射</summary>
        public static readonly Dictionary<int, string> FaultCodeMap = new()
        {
            [1] = "冬季防冻保护",
            [2] = "空调水流量不足",
            [3] = "热水流量不足",
            [4] = "空调循环水泵过载",
            [5] = "热水循环水泵过载",
            [6] = "热水系统电加热过载",
            [7] = "排气温度传感器故障",
            [8] = "热回收出口温度传感器故障",
            [9] = "中盘温度传感器故障",
            [10] = "出盘温度传感器故障",
            [11] = "经济器进口温度传感器故障",
            [12] = "经济器出口温度传感器故障",
            [13] = "吸气温度传感器故障",
            [14] = "空调进水温度传感器故障",
            [15] = "空调出水温度传感器故障",
            [16] = "环境温度传感器故障",
            [17] = "热水进水温度传感器故障",
            [18] = "热水出水温度传感器故障",
            [19] = "热水温度传感器故障",
            [20] = "高压传感器故障",
            [21] = "低压传感器故障",
            [22] = "高压开关故障",
            [23] = "运行进出水温差过大",
            [24] = "进出水温度传感器插反",
            [25] = "环境温度保护",
            [26] = "制冷防冻保护",
            [27] = "排气过热度保护",
            [28] = "高压故障",
            [29] = "低压故障",
            [30] = "制冷剂泄漏",
            [31] = "排气温度过高",
            [32] = "吸气温度过高",
            [33] = "吸气过热度过小",
            [34] = "排温异常",
            [35] = "压缩机驱动故障",
            [36] = "上风机驱动故障",
            [37] = "压缩机驱动通讯故障",
            [38] = "风机驱动通讯故障",
            [39] = "压缩机驱动输入电流过载",
            [40] = "压缩机驱动输出电流过载",
            [41] = "驱动板和主控板通信故障",
            [42] = "PFC驱动故障",
            [43] = "IPM过热保护",
            [44] = "压缩机电流过大保护",
            [45] = "交流电流过大保护",
            [46] = "交流电压过低保护",
            [47] = "交流电压过高保护",
            [48] = "直流母线过压",
            [49] = "直流母线欠压",
            [50] = "压缩机软过流",
            [51] = "压缩机硬过流",
            [52] = "压缩机缺相",
            [53] = "AD噪声过大/偏置错误",
            [54] = "压缩机堵转",
            [55] = "速度推定错误或控制异常",
            [56] = "PFC Fo",
            [57] = "热泵主机和4G LTE通讯故障",
            [58] = "热泵主机和电量模块通讯故障",
            [59] = "热泵主机和MixPad通讯故障",
            [60] = "机组电源异常",
            [61] = "生活热水防冻保护",
            [62] = "下风机驱动故障",
            [63] = "热水上温度传感器故障",
            [64] = "热水中温度传感器故障",
            [65] = "热水下温度传感器故障",
            [66] = "热水进水温度过低",
            [100] = "激活变未激活",
        };

        /// <summary>热泵主机故障清除方式映射</summary>
        public static readonly Dictionary<int, string> FaultResetMethodMap = new()
        {
            [1] = "自动复位（锁定10分钟）",
            [2] = "手动复位", [3] = "手动复位", [4] = "手动复位",
            [5] = "手动复位", [6] = "手动复位",
            [7] = "自动复位", [8] = "自动复位", [9] = "自动复位",
            [10] = "自动复位", [11] = "自动复位", [12] = "自动复位",
            [13] = "自动复位", [14] = "自动复位", [15] = "自动复位",
            [16] = "自动复位", [17] = "自动复位", [18] = "自动复位",
            [19] = "自动复位", [20] = "自动复位", [21] = "自动复位",
            [22] = "手动复位（锁定10分钟后自动复位）",
            [23] = "自动复位", [24] = "手动复位", [25] = "自动复位",
            [26] = "第三次手动复位", [27] = "第三次手动复位",
            [28] = "第三次手动复位", [29] = "第三次手动复位",
            [30] = "手动复位（锁定10分钟后自动复位）",
            [31] = "第三次手动复位", [32] = "第三次手动复位",
            [33] = "第三次手动复位",
            [34] = "手动复位（锁定10分钟后自动复位）",
            [35] = "第三次手动复位", [36] = "第三次手动复位",
            [37] = "第三次手动复位", [38] = "第三次手动复位",
            [39] = "手动复位（锁定10分钟后自动复位）",
            [40] = "手动复位（锁定10分钟后自动复位）",
            [41] = "自动复位", [42] = "自动复位", [43] = "自动复位",
            [44] = "协议未定义", [45] = "协议未定义",
            [46] = "自动复位", [47] = "自动复位",
            [48] = "协议未定义", [49] = "协议未定义", [50] = "协议未定义",
            [51] = "协议未定义", [52] = "协议未定义", [53] = "协议未定义",
            [54] = "协议未定义", [55] = "协议未定义", [56] = "协议未定义",
            [57] = "协议未定义", [58] = "协议未定义", [59] = "协议未定义",
            [60] = "第三次手动复位", [61] = "自动复位",
            [62] = "第三次手动复位", [63] = "自动复位",
            [64] = "自动复位", [65] = "自动复位", [66] = "自动复位",
            [100] = "由激活或限制使用命令复位"
        };

        /// <summary>产品型号映射</summary>
        public static readonly Dictionary<string, string> ProductModelMap = new()
        {
            ["MS-W1"] = "MixStation W1",
            ["MS-S1"] = "MixStation W135",
            ["MS-W260"] = "MixStation W260",
            ["MS-W180"] = "MixStation W180",
            ["MS-W300"] = "MixStation W300",
            ["MS-W350"] = "MixStation W350",
        };

        /// <summary>清除设备信息命令映射</summary>
        public static readonly Dictionary<ushort, string> ClearDeviceCmdMap = new()
        {
            [0x0000] = "所有参数恢复出厂设置",
            [0x0001] = "服务密码参数恢复出厂设置",
            [0x0002] = "工厂密码参数恢复出厂设置",
            [0x0003] = "清除485网络设备地址表",
        };
    }
}

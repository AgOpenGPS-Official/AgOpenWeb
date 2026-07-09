// EasyEDA Pro extension — "Color nets by IC"
// Colors each IC's LOCAL nets a distinct color so the scattered support
// passives (pull-ups, feedback R, crystal caps, filters, split-term,
// charge-pump) can be found by following their colored ratsnest.
//
// NOTE: pure decoupling caps sit on the shared +3V3/GND rails, which are
// intentionally NOT colored (they touch every chip) — use schematic
// cross-select for those. This handles everything on IC-unique nets.
//
// Generated from Full-board_2026-07-08.net. Run from the PCB editor.
//
// setNetColor takes a {r,g,b,alpha} object (alpha 0..1), NOT a hex string/int
// (confirmed via getNetColor readback). After running, REFRESH THE BROWSER —
// the color is written but the canvas doesn't repaint until a reload.

// IC → color legend (tell them apart on screen):
//  U7 STM32=red  U19 CM4=green  U5 M.2=blue  U1 buck=orange  U2 eFuse=purple
//  U4 buckNVMe=cyan  U8/9/10 CAN=magenta/lime/pink  U11 RS485=teal
//  U12 RS232=brown  U16 UM982=olive  P2 ArduSimple=navy
const NET_COLORS: Record<string, string> = {
  // --- U7 STM32 (red) : peripheral + support nets ---
  "OSC_IN":"#e6194B","OSC_OUT":"#e6194B","NRST":"#e6194B","WAS_ADC":"#e6194B",
  "ISENSE_ADC":"#e6194B","BTN_RST":"#e6194B","PIEZO_PWM":"#e6194B","LED_STAT":"#e6194B",
  "PAGE_A":"#e6194B","PAGE_B":"#e6194B","OLED_SCL":"#e6194B","OLED_SDA":"#e6194B",
  "SWDIO":"#e6194B","SWCLK":"#e6194B","PPS":"#e6194B","CM_USB2_P":"#e6194B","CM_USB2_N":"#e6194B",
  "UART1_TX":"#e6194B","UART1_RX":"#e6194B","UART2_TX":"#e6194B","UART2_RX":"#e6194B",
  "UART4_TX":"#e6194B","UART4_RX":"#e6194B","UART5_TX":"#e6194B","UART5_RX":"#e6194B",
  "LPUART1_TX":"#e6194B","LPUART1_RX":"#e6194B","RS485_DE":"#e6194B",
  "CAN1_TX":"#e6194B","CAN1_RX":"#e6194B","CAN2_TX":"#e6194B","CAN2_RX":"#e6194B",
  "CAN3_TX":"#e6194B","CAN3_RX":"#e6194B","STEER_EN":"#e6194B","MOT_DIR":"#e6194B",
  "PWM_MOTA":"#e6194B","PWM_MOTB":"#e6194B","PWM_VALVE1":"#e6194B","PWM_VALVE2":"#e6194B",
  "PI_PWR_EN":"#e6194B","PI_FLT":"#e6194B","U7_39":"#e6194B",
  // --- U19 CM4 (green) : PCIe / Ethernet / console / LED ---
  "PCIE_TX_P":"#3cb44b","PCIE_TX_N":"#3cb44b","PCIE_RX_P":"#3cb44b","PCIE_RX_N":"#3cb44b",
  "PCIE_CK_P":"#3cb44b","PCIE_CK_N":"#3cb44b","PCIE_NRST":"#3cb44b","PCIE_CLKREQ":"#3cb44b",
  "ETH0_P":"#3cb44b","ETH0_N":"#3cb44b","ETH1_P":"#3cb44b","ETH1_N":"#3cb44b",
  "ETH2_P":"#3cb44b","ETH2_N":"#3cb44b","ETH3_P":"#3cb44b","ETH3_N":"#3cb44b",
  "NLED1":"#3cb44b","NLED2":"#3cb44b","CON_TXD":"#3cb44b","CON_RXD":"#3cb44b",
  // --- U1 buck-main (orange) ---
  "SW_BUCK1":"#f58231","BOOT1":"#f58231","FB1":"#f58231","COMP1":"#f58231","RT1":"#f58231","EN1":"#f58231",
  // --- U2 eFuse (purple) ---
  "DVDT":"#911eb4","ILIM":"#911eb4",
  // --- U4 buck-NVMe (cyan) ---
  "BOOT5":"#42d4f4","FB5":"#42d4f4","EN5":"#42d4f4",
  // --- CAN transceivers ---
  "CAN1_HT":"#f032e6","CAN1_LT":"#f032e6",   // U8 magenta
  "CAN2_HT":"#bfef45","CAN2_LT":"#bfef45",   // U9 lime
  "CAN3_HT":"#fabed4","CAN3_LT":"#fabed4",   // U10 pink
  // --- U11 RS485 (teal) ---
  "RS485_A":"#469990","RS485_B":"#469990",
  // --- U12 RS232 (brown) : charge pump + field ---
  "SP_C1P":"#9A6324","SP_C1N":"#9A6324","SP_C2P":"#9A6324","SP_C2N":"#9A6324",
  "SP_VP":"#9A6324","SP_VN":"#9A6324",
  "NMEA_TX":"#9A6324","NMEA_RX":"#9A6324","EXT232_TX":"#9A6324","EXT232_RX":"#9A6324",
};

function toColor(hex: string) {
  return {
    r: parseInt(hex.slice(1, 3), 16),
    g: parseInt(hex.slice(3, 5), 16),
    b: parseInt(hex.slice(5, 7), 16),
    alpha: 1,
  };
}

export async function activate(): Promise<void> {
  let ok = 0, miss = 0;
  for (const net of Object.keys(NET_COLORS)) {
    try {
      await eda.pcb_Net.setNetColor(net, toColor(NET_COLORS[net]) as any);
      ok++;
    } catch (e) {
      miss++;
    }
  }
  eda.sys_ToastMessage.showMessage(
    `Colored ${ok} nets by IC` + (miss ? ` (${miss} skipped)` : ``),
    ESYS_ToastMessageType.INFO,
  );
}

// Optional companion: clear all coloring back to default.
export async function clearColors(): Promise<void> {
  eda.pcb_Net.unhighlightAllNets?.();
  eda.sys_ToastMessage.showMessage("Cleared net highlights", ESYS_ToastMessageType.INFO);
}

// ===== AgOpenWeb — Color nets by IC (paste into EasyEDA Pro "Run Script") =====
// Colors each IC's local nets a distinct color so scattered support passives
// can be found by following their colored ratsnest. Run from the PCB editor.
// Decoupling caps live on shared +3V3/GND (not colored) — cross-select those.
//
// USAGE: run this, then REFRESH THE BROWSER (F5). setNetColor writes the color
// (swatches update in the net list) but doesn't repaint the canvas until a
// reload — after the refresh, ratlines + pads show their IC color.
//
// setNetColor takes a {r,g,b,alpha} object (alpha 0..1), NOT a hex string/int.
//
// Legend: U7 STM32=red · U19 CM4=green · U1 buck=orange · U2 eFuse=purple
//         U4 buckNVMe=cyan · CAN1/2/3=magenta/lime/pink · U11 RS485=teal
//         U12 RS232=brown

(async () => {
  // setNetColor wants a {r,g,b,alpha} object (confirmed via getNetColor readback),
  // NOT a "#hex" string or int. alpha is 0..1.

  const NET_COLORS = {
    // U7 STM32 (red)
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
    // U19 CM4 (green)
    "PCIE_TX_P":"#3cb44b","PCIE_TX_N":"#3cb44b","PCIE_RX_P":"#3cb44b","PCIE_RX_N":"#3cb44b",
    "PCIE_CK_P":"#3cb44b","PCIE_CK_N":"#3cb44b","PCIE_NRST":"#3cb44b","PCIE_CLKREQ":"#3cb44b",
    "ETH0_P":"#3cb44b","ETH0_N":"#3cb44b","ETH1_P":"#3cb44b","ETH1_N":"#3cb44b",
    "ETH2_P":"#3cb44b","ETH2_N":"#3cb44b","ETH3_P":"#3cb44b","ETH3_N":"#3cb44b",
    "NLED1":"#3cb44b","NLED2":"#3cb44b","CON_TXD":"#3cb44b","CON_RXD":"#3cb44b",
    // U1 buck-main (orange)
    "SW_BUCK1":"#f58231","BOOT1":"#f58231","FB1":"#f58231","COMP1":"#f58231","RT1":"#f58231","EN1":"#f58231",
    // U2 eFuse (purple)
    "DVDT":"#911eb4","ILIM":"#911eb4",
    // U4 buck-NVMe (cyan)
    "BOOT5":"#42d4f4","FB5":"#42d4f4","EN5":"#42d4f4",
    // CAN xcvrs
    "CAN1_HT":"#f032e6","CAN1_LT":"#f032e6",
    "CAN2_HT":"#bfef45","CAN2_LT":"#bfef45",
    "CAN3_HT":"#fabed4","CAN3_LT":"#fabed4",
    // U11 RS485 (teal)
    "RS485_A":"#469990","RS485_B":"#469990",
    // U12 RS232 (brown)
    "SP_C1P":"#9A6324","SP_C1N":"#9A6324","SP_C2P":"#9A6324","SP_C2N":"#9A6324",
    "SP_VP":"#9A6324","SP_VN":"#9A6324",
    "NMEA_TX":"#9A6324","NMEA_RX":"#9A6324","EXT232_TX":"#9A6324","EXT232_RX":"#9A6324",
  };

  const col = (h) => ({
    r: parseInt(h.slice(1, 3), 16),
    g: parseInt(h.slice(3, 5), 16),
    b: parseInt(h.slice(5, 7), 16),
    alpha: 1,
  });
  let ok = 0, miss = 0;
  for (const net of Object.keys(NET_COLORS)) {
    try { await eda.pcb_Net.setNetColor(net, col(NET_COLORS[net])); ok++; }
    catch (e) { miss++; }
  }
  // No refresh() in the API — a view nudge repaints the canvas so colors show
  // without a manual browser reload. zoomToBoardOutline re-renders (and zoom-fits;
  // swap for save() or navigateToCoordinates if you'd rather keep your zoom).
  try { await eda.pcb_Document.zoomToBoardOutline(); } catch (e) {}

  // toast type is a raw number in the Run Script runtime (2 = info), not an enum
  try {
    eda.sys_ToastMessage.showMessage(
      "Colored " + ok + " nets by IC" + (miss ? " (" + miss + " skipped)" : ""), 2);
  } catch (e) { /* toast optional; coloring already applied */ }
})();

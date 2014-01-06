﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Winterdom.Viasfora.Design {
  public partial class ZoomTrackBar : UserControl {

    public int Value {
      get {
        return GetValue();
      }
      set {
        SetValue(value);
      }
    }
    public ZoomTrackBar() {
      InitializeComponent();
    }
    public int GetValue() {
      double trackbarValue = trackBar.Value;
      double yp = trackbarValue;
      double zoomValue = ((yp * yp) / (50 * 50)) + 20;
      return (int)Math.Round(zoomValue);
    }
    public void SetValue(int zoomValue) {
      if ( zoomValue == 0 ) zoomValue = 20;
      double trackbarValue = 50 * Math.Sqrt(zoomValue-20);
      trackBar.Value = (int)Math.Round(trackbarValue);
    }

    public void OnTrackBarValueChanged(object sender, EventArgs e) {
      label.Text = String.Format("{0} %", GetValue());
    }
  }
}

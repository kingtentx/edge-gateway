using EdgeGateway.Model;

namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    internal bool RealtimeBrowserSmoke { get; set; }

    private async Task SmokeRealtimeBrowserAsync()
    {
        if (!_smokeTest || _application.IsRunning) throw new InvalidOperationException("RealtimeBrowser requires an isolated stopped host.");
        _refreshTimer.Stop();
        var original = Clone(_configuration);
        var originalSize = Size;
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("RealtimeBrowser: " + message); }
        async Task WaitFor(Func<bool> condition)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Require(condition(), "simulator did not produce a fresh sample");
        }
        try
        {
            var fixture = Clone(original);
            fixture.AutoStart = false; fixture.Mqtt.Enabled = false;
            fixture.Channels = []; fixture.Databases = []; fixture.DatabaseWriteGroups = [];
            fixture.Logging.LogAcquisitionSamples = false;
            var a = new DeviceConfiguration { Name = "一号设备", Group = "生产一组", DriverId = "simulator", PollIntervalMs = 100,
                Tags = Enumerable.Range(1, 25).Select(i => new TagConfiguration
                { Name = $"温度 {i:00}", Address = "ramp", DataType = TagDataType.Float64, Writable = true }).ToList() };
            var b = new DeviceConfiguration { Name = "二号设备", Group = "生产二组", DriverId = "simulator",
                Tags = [new() { Name = "压力", Address = "constant:1", DataType = TagDataType.Float64 }] };
            // Reproduce the reported all-devices view: 4 + 6 + 6 points, including
            // identical point names on different devices and no samples yet.
            fixture.Devices = Enumerable.Range(0, 3).Select(index => new DeviceConfiguration
            {
                Name = $"截图设备 {index}", Group = "截图分组", DriverId = "simulator",
                Tags = Enumerable.Range(0, index == 0 ? 4 : 6).Select(tag => new TagConfiguration
                { Name = $"点位 {tag}", Address = "constant:1", DataType = TagDataType.Float64 }).ToList()
            }).ToList();
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            ShowPage("realtime"); SelectRealtimeDevice(null); RefreshRealtime([]); PerformLayoutRecursively(this);
            var pager = GridPagination.GetPager(_realtimeGrid);
            var expected = fixture.Devices.SelectMany(device => device.Tags.Select(tag => (device.Id, tag.Id))).ToHashSet();
            var displayed = _realtimeGrid.Rows.Select(row => ((SampleRow)row.DataBoundItem!).Sample)
                .Select(sample => (sample.DeviceId, sample.TagId)).ToHashSet();
            Require(pager.PageSize == 20 && pager.Total == 16 && displayed.SetEquals(expected),
                "all-devices default page omitted one of the 16 configured points");
            SaveEditorScreenshot(this, "realtime-browser", "all-devices-16");
            pager.PageSize = 10;
            displayed.Clear();
            foreach (var page in new[] { 1, 2 })
            {
                pager.Current = page;
                foreach (var row in _realtimeGrid.Rows)
                {
                    var sample = ((SampleRow)row.DataBoundItem!).Sample;
                    Require(displayed.Add((sample.DeviceId, sample.TagId)), "duplicate point across pages");
                }
            }
            Require(displayed.SetEquals(expected), "all-devices pagination omitted a device or point");
            pager.PageSize = 20; pager.Current = 1;
            fixture.Devices = [a, b]; fixture.DeviceGroups = [a.Group, b.Group, "空分组"];
            await CommitConfigurationAsync(fixture, replaceCloudDraft: true);
            ShowPage("realtime"); RefreshRealtime([]); PerformLayoutRecursively(this);
            Require(pager.Total == 26, "configured unsampled points are missing");
            Require(((SampleRow)_realtimeGrid.Rows[0].DataBoundItem!).Time == "—" && !_writeButton.Enabled, "unsampled point looks live");
            var tree = _realtimePageView.DeviceTree;
            var groupFilter = _realtimePageView.GroupSelector;
            groupFilter.SelectedItem = groupFilter.Items.Cast<object>().Single(item => item.ToString() == b.Group);
            Require(pager.Total == 1, "group filter");
            groupFilter.SelectedItem = groupFilter.Items.Cast<object>().Single(item => item.ToString() == "空分组");
            Require(pager.Total == 0, "empty group");
            groupFilter.SelectedIndex = 0;
            tree.SelectID("group:" + a.Group, false);
            Require(pager.Total == 25, "group tree selection");
            tree.SelectID(b.Id.ToString("D"), false);
            Require(pager.Total == 1 && _realtimeDeviceId == b.Id, "device tree selection");
            SelectRealtimeDevice(a.Id); RefreshRealtime([]);
            _realtimeSearch.Text = "温度 03";
            Require(pager.Total == 1, "point name search");
            _realtimeSearch.Text = "missing"; Require(pager.Total == 0, "empty search");
            _realtimeSearch.Clear();
            var time = DateTimeOffset.Now.AddMinutes(-1);
            var samples = a.Tags.Select(tag => new TelemetrySample(a.Id, a.Name, tag.Id, tag.Name, tag.Address,
                10D, 10D, SampleQuality.Good, time, tag.Unit)).ToArray();
            DeviceRuntimeStatus[] online = [new(a.Id, a.Name, DeviceState.Running, time, 0, null)];
            RefreshRealtime(samples, online);
            Require(_realtimeDeviceNodes[a.Id].IconSvg == "PlayCircleFilled"
                && _realtimeDeviceNodes[a.Id].Text == a.Name, "running icon/name");
            pager.Current = 2;
            _realtimeGrid.CurrentCell = _realtimeGrid.Rows[3].Cells["Value"];
            var selected = ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId;
            var source = _realtimeGrid.DataSource;
            var node = _realtimeDeviceNodes[a.Id];
            RefreshRealtime(samples.Select(sample => sample with { Value = 20D, Timestamp = time.AddSeconds(1) }).ToArray(), online);
            Require(pager.Current == 2 && ReferenceEquals(source, _realtimeGrid.DataSource)
                && ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId == selected
                && ((SampleRow)_realtimeGrid.CurrentRow.DataBoundItem!).Value == "20"
                && ReferenceEquals(node, _realtimeDeviceNodes[a.Id]), "refresh lost page, selected ID or tree node");
            RefreshRealtime(samples.Select(sample => sample with { Value = 30D, Timestamp = time.AddSeconds(2),
                Quality = SampleQuality.Bad, Error = "模拟离线" }).ToArray(), [new(a.Id, a.Name, DeviceState.Faulted, time, 1, "模拟离线")]);
            Require(_realtimeDeviceNodes[a.Id].IconSvg == "ExclamationCircleFilled", "fault icon");
            var held = (SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!;
            Require(held.Value == "20" && held.Sample.Timestamp == time.AddSeconds(1)
                && held.Sample.Quality == SampleQuality.Bad && held.Error.Contains("模拟离线") && !_writeButton.Enabled,
                "offline advanced value/time or hid failure");
            SaveEditorScreenshot(this, "realtime-browser", "offline");
            BindDeviceChoices();
            Require(_realtimeDeviceId == a.Id, "configuration rebind lost selected device");
            await _application.StartDeviceAsync(a.Id);
            await WaitFor(() => _application.GetLatest().Count(sample => sample.DeviceId == a.Id && sample.Quality == SampleQuality.Good) == 25);
            RefreshRealtime(_application.GetLatest());
            Require(((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.Timestamp > held.Sample.Timestamp
                && _writeButton.Enabled && !_application.IsDeviceRunning(b.Id), "online recovery or write availability");
            await _application.StopDeviceAsync(a.Id);
            RefreshRealtime(_application.GetLatest());
            Require(_realtimeDeviceNodes[a.Id].IconSvg == "PauseCircleOutlined", "stopped icon");
            var stopped = ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample;
            await Task.Delay(220); RefreshRealtime(_application.GetLatest());
            Require(((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.Timestamp == stopped.Timestamp
                && !_writeButton.Enabled, "stopped simulator advanced time or enabled write");
            await _application.StartDeviceAsync(a.Id);
            await WaitFor(() => _application.GetLatest().Any(sample => sample.DeviceId == a.Id && sample.Timestamp > stopped.Timestamp));
            RefreshRealtime(_application.GetLatest());
            Require(((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.Timestamp > stopped.Timestamp, "restart did not resume values");
            var splitter = _realtimePageView.BrowserSplitter;
            void DragSplitter(int delta)
            {
                var rect = splitter.SplitterRectangle;
                var x = rect.Left + rect.Width / 2;
                var y = rect.Top + rect.Height / 2;
                void Mouse(string method, MouseButtons button, int mouseX) =>
                    typeof(AntdUI.Splitter).GetMethod(method,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(splitter, [new MouseEventArgs(button, 1, mouseX, y, 0)]);
                Mouse("OnMouseMove", MouseButtons.None, x);
                Mouse("OnMouseDown", MouseButtons.Left, x);
                Mouse("OnMouseMove", MouseButtons.Left, x + delta);
                Mouse("OnMouseUp", MouseButtons.Left, x + delta);
            }
            ClientSize = new Size(UiTheme.Scale(this, 1400), UiTheme.Scale(this, 720));
            await Task.Delay(100); PerformLayoutRecursively(this);
            var initialDistance = splitter.SplitterDistance;
            DragSplitter(UiTheme.Scale(this, 60));
            Require(splitter.SplitterDistance >= initialDistance + UiTheme.Scale(this, 58), "drag right did not enlarge tree");
            var enlarged = splitter.SplitterDistance;
            DragSplitter(-UiTheme.Scale(this, 24));
            Require(splitter.SplitterDistance <= enlarged - UiTheme.Scale(this, 22), "drag left did not shrink tree");
            var preferredDistance = splitter.SplitterDistance;
            Console.WriteLine($"Realtime splitter drag: {initialDistance} -> {enlarged} -> {preferredDistance}.");
            ShowPage("devices"); ShowPage("realtime");
            Require(splitter.SplitterDistance == preferredDistance, "page switch reset splitter");
            foreach (var width in new[] { 1400, 1008, 1400 })
            {
                ClientSize = new Size(UiTheme.Scale(this, width), UiTheme.Scale(this, 720));
                await Task.Delay(100); PerformLayoutRecursively(this);
                BringIntoViewAndAssert(tree, "realtime device tree");
                BringIntoViewAndAssert(groupFilter, "realtime group filter");
                BringIntoViewAndAssert(_realtimeSearch, "realtime point search");
                BringIntoViewAndAssert(_writeButton, "realtime write button");
                Require(tree.RectangleToScreen(tree.ClientRectangle).Right < _realtimeGrid.RectangleToScreen(_realtimeGrid.ClientRectangle).Left,
                    "left/right panes overlap");
                var expectedDistance = Math.Clamp(preferredDistance, splitter.Panel1MinSize,
                    splitter.ClientSize.Width - ((SplitContainer)splitter).SplitterWidth - splitter.Panel2MinSize);
                Require(Math.Abs(splitter.SplitterDistance - expectedDistance) <= 1, "resize lost preferred width or minimum constraints");
                var selectedBefore = ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId;
                await VerifyPageRefreshStabilityAsync(_realtimePageView, "realtime browser " + width);
                Require(pager.Current == 2 && ((SampleRow)_realtimeGrid.CurrentRow!.DataBoundItem!).Sample.TagId == selectedBefore,
                    "timer refresh lost selection or page");
                Require(splitter.SplitterDistance == expectedDistance, "refresh reset splitter");
                SaveEditorScreenshot(this, "realtime-browser", width == 1008 ? "narrow" : "wide");
            }
            Console.WriteLine($"RealtimeBrowser smoke passed: DeviceDpi={DeviceDpi}; groups, tree, point search, unsampled points, offline retention/quality, simulator stop/restart, write guard, page/selection and width round trip. Status icons, official splitter drag and width retention passed. No hardware or external database.");
        }
        finally
        {
            _refreshTimer.Stop(); await _application.StopAsync();
            Size = originalSize;
            await RestoreConfigurationForSmokeAsync(original);
        }
    }
}

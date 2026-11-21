const pluginId = '0d3b79db-1562-4b6d-a2f4-85be34f16e45';

export default function (view) {
    const form = view.querySelector('#PosterDBConfigForm');
    const statusEl = view.querySelector('#PosterDBStatus');

    function setStatus(msg, ok) {
        statusEl.textContent = msg;
        statusEl.style.color = ok ? 'var(--theme-green, green)' : 'var(--theme-red, red)';
    }

    view.addEventListener('pageshow', function () {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#Email').value = config.Email || '';
            view.querySelector('#Password').value = config.Password || '';
            statusEl.textContent = '';
            Dashboard.hideLoadingMsg();
        });
    });

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.Email = view.querySelector('#Email').value;
            config.Password = view.querySelector('#Password').value;
            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
        return false;
    });

    view.querySelector('#TestConnectionBtn').addEventListener('click', function () {
        statusEl.textContent = 'Testing…';
        statusEl.style.color = '';

        // Save first so the server uses the latest credentials
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.Email = view.querySelector('#Email').value;
            config.Password = view.querySelector('#Password').value;
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(function () {
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Plugins/PosterDB/TestConnection')
            });
        }).then(function () {
            setStatus('Connected successfully.', true);
        }).catch(function () {
            setStatus('Connection failed — check credentials.', false);
        });
    });
}

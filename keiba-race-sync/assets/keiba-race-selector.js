/**
 * レース選択UI: 競馬場を選ぶ → レース番号を選ぶ → 出走表/結果を表示。
 *
 * 出走表のHTMLはサーバー側（PHPの描画関数）で組み立てたものを取得して差し込む。
 * 表の組み立てをJS側にも書くと、項目追加のたびに2箇所直すことになるため。
 */
(function () {
    'use strict';

    var endpoint = (window.keibaRaceSelector && window.keibaRaceSelector.endpoint) || '';

    document.querySelectorAll('.keiba-selector').forEach(function (root) {
        var trackButtons = root.querySelectorAll('.keiba-track-btn');
        var stepRaces = root.querySelector('.keiba-step-races');
        var raceGrids = root.querySelectorAll('.keiba-race-grid');
        var stepDetail = root.querySelector('.keiba-step-detail');
        var detailBody = root.querySelector('.keiba-detail-body');
        var detailHeading = root.querySelector('.keiba-detail-heading');

        if (!stepRaces || !stepDetail || !detailBody) {
            return;
        }

        function selectTrack(code) {
            trackButtons.forEach(function (btn) {
                btn.classList.toggle('is-active', btn.dataset.track === code);
            });
            raceGrids.forEach(function (grid) {
                grid.hidden = grid.dataset.track !== code;
            });
            stepRaces.hidden = false;

            // 競馬場を切り替えたら、前の競馬場のレース表示は消す
            // （別競馬場の出走表が残っていると誤読につながるため）。
            stepDetail.hidden = true;
            detailBody.innerHTML = '';
            root.querySelectorAll('.keiba-race-btn').forEach(function (btn) {
                btn.classList.remove('is-active');
            });

            stepRaces.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }

        function selectRace(button) {
            var raceKey = button.dataset.raceKey;
            if (!raceKey || !endpoint) {
                return;
            }

            root.querySelectorAll('.keiba-race-btn').forEach(function (btn) {
                btn.classList.toggle('is-active', btn === button);
            });

            stepDetail.hidden = false;
            detailBody.innerHTML = '<p class="keiba-loading">読み込み中...</p>';

            var url = endpoint + (endpoint.indexOf('?') === -1 ? '?' : '&') +
                'race_key=' + encodeURIComponent(raceKey);

            fetch(url, { credentials: 'same-origin' })
                .then(function (res) {
                    if (!res.ok) {
                        throw new Error('HTTP ' + res.status);
                    }
                    return res.json();
                })
                .then(function (data) {
                    if (detailHeading) {
                        detailHeading.textContent = data.title || '出走表';
                    }
                    detailBody.innerHTML = data.html || '';
                    stepDetail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                })
                .catch(function () {
                    detailBody.innerHTML =
                        '<p class="keiba-error">レース情報を読み込めませんでした。' +
                        '時間をおいて再度お試しください。</p>';
                });
        }

        trackButtons.forEach(function (btn) {
            btn.addEventListener('click', function () {
                selectTrack(btn.dataset.track);
            });
        });

        // レース番号ボタンは競馬場ごとに多数あるため、親要素で受ける（委譲）。
        stepRaces.addEventListener('click', function (event) {
            var button = event.target.closest('.keiba-race-btn');
            if (button) {
                selectRace(button);
            }
        });

        // 開催が1場だけなら、選ぶ手間を省いて最初から開いておく。
        if (trackButtons.length === 1) {
            selectTrack(trackButtons[0].dataset.track);
        }
    });
})();

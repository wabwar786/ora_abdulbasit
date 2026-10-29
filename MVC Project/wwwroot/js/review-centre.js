/* ORA Review Centre. Uses real dbo.HotelFeedback rows; no sample data.
   Does not reference, hide, modify or replace any existing iframe. */
(function () {
    'use strict';

    var root = document.getElementById('oraReviewCentre');
    if (!root) return;
    var board = document.getElementById('board');
    var reviews = [];
    var officialScores = null;
    var state = { search: '', score: 'all', tabs: {}, expanded: {}, drafts: {} };
    var sending = false;
    // Only these four channels are displayed, always in this exact order.
    // Records for other channels stay in HotelFeedback and are not modified.
    var platforms = [
        { key: 'booking', name: 'Booking.com', short: 'B.', brand: '#003580', max: 10 },
        { key: 'expedia', name: 'Expedia', short: 'E', brand: '#00355f', max: 10 },
        { key: 'airbnb', name: 'Airbnb', short: 'Ab', brand: '#e0484d', max: 5 },
        { key: 'website', name: 'Website', short: 'W', brand: '#0f766e', max: 10 }
    ];
    var allowedPlatforms = { booking: true, expedia: true, airbnb: true, website: true };

    function normalize(v) { return String(v == null ? '' : v).trim().toLowerCase().replace(/[^a-z0-9]/g, ''); }
    function platformKey(name) {
        var v = normalize(name);
        if (v.indexOf('booking') >= 0) return 'booking';
        if (v.indexOf('agoda') >= 0) return 'agoda';
        if (v.indexOf('expedia') >= 0) return 'expedia';
        if (v.indexOf('google') >= 0) return 'google';
        if (v.indexOf('trip') >= 0 || v.indexOf('ctrip') >= 0) return 'trip';
        if (v.indexOf('airbnb') >= 0) return 'airbnb';
        if (v === 'website' || v === 'direct' || v === 'ora') return 'website';
        return 'other:' + (v || 'unknown');
    }
    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    // Converts the JSON-encoded reply bodies in older HotelFeedback records
    // to readable text. The C# response is normalized too; this also protects
    // the UI when older data/page versions are cached.
    function cleanReplyText(raw) {
        var text = String(raw == null ? '' : raw).trim();
        if (!text) return '';
        function extract(token, inReply, depth) {
            if (depth > 6 || token == null) return '';
            if (typeof token === 'string') return inReply ? token : '';
            if (Array.isArray(token)) {
                for (var i = 0; i < token.length; i++) {
                    var fromItem = extract(token[i], inReply, depth + 1);
                    if (fromItem) return fromItem;
                }
                return '';
            }
            if (typeof token !== 'object') return '';
            var props = Object.keys(token);
            function get(key) {
                var match = props.find(function (name) { return name.toLowerCase() === key.toLowerCase(); });
                return match === undefined ? undefined : token[match];
            }
            var replyKeys = ['reply', 'reply_text', 'replyText', 'reply_body', 'replyBody',
                'hotel_reply', 'hotelReply', 'owner_response', 'ownerResponse', 'answer', 'response'];
            for (var a = 0; a < replyKeys.length; a++) {
                var reply = get(replyKeys[a]);
                if (reply !== undefined) {
                    var fromReply = extract(reply, true, depth + 1);
                    if (fromReply) return fromReply;
                }
            }
            if (inReply) {
                var textKeys = ['text', 'content', 'body', 'message', 'value'];
                for (var b = 0; b < textKeys.length; b++) {
                    var nested = get(textKeys[b]);
                    if (nested !== undefined) {
                        var fromText = extract(nested, true, depth + 1);
                        if (fromText) return fromText;
                    }
                }
            }
            for (var c = 0; c < 2; c++) {
                var envelope = get(c === 0 ? 'data' : 'result');
                if (envelope !== undefined) {
                    var fromEnvelope = extract(envelope, false, depth + 1);
                    if (fromEnvelope) return fromEnvelope;
                }
            }
            return '';
        }
        for (var pass = 0; pass < 3; pass++) {
            if (!/^[{[\"]/.test(text)) break;
            try {
                var parsed = JSON.parse(text);
                if (typeof parsed === 'string') { text = parsed.trim(); continue; }
                text = String(extract(parsed, false, 0) || '').trim();
                if (!text) return '';
            } catch (_) {
                if (/^[{[]/.test(text)) return '';
                break;
            }
        }
        return text.replace(/\\r\\n/g, '\n').replace(/\\n/g, '\n').replace(/\\r/g, '\n');
    }
    function prepareReviews() {
        reviews.forEach(function (review, index) {
            review._index = index;
            review.ReplyText = cleanReplyText(review.ReplyText);
            // Non-empty reply content is evidence of an existing reply.
            // Preserve the OTA's IsReplied flag if it is already true.
            if (review.ReplyText) {
                review.IsReplied = true;
                review.CanReply = false;
            }
        });
    }
    function percentage(p) { return Math.min(100, Math.max(0, Math.round(p * 100))); }
    function ratingColor(p) { return p >= .85 ? 'var(--good)' : p >= .7 ? 'var(--warn)' : 'var(--bad)'; }
    function band(p) { return p >= .85 ? 'good' : p >= .7 ? 'avg' : 'low'; }
    function ratingLabel(p) { return p >= .9 ? 'Exceptional' : p >= .85 ? 'Excellent' : p >= .8 ? 'Very Good' : p >= .7 ? 'Good' : 'Needs Attention'; }
    function stars(p) { var n = Math.min(5, Math.max(0, Math.round(p * 5))); return '★'.repeat(n) + '☆'.repeat(5 - n); }
    function initials(value) {
        var bits = String(value || 'Guest').trim().split(/\s+/).slice(0, 2);
        return bits.map(function (v) { return v.charAt(0); }).join('').toUpperCase();
    }
    function prettyDate(value) {
        if (!value) return 'Date unavailable';
        var date = new Date(value);
        if (isNaN(date.getTime())) return 'Date unavailable';
        return date.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
    }
    function scorePct(item) {
        return item.Score10 == null ? null : Math.max(0, Math.min(1, Number(item.Score10) / 10));
    }
    function officialChannel(key) {
        var scores = officialScores && officialScores.Channels;
        return scores && Object.prototype.hasOwnProperty.call(scores, key) ? scores[key] : null;
    }
    function displayRating(score10, max) {
        if (score10 == null || !Number.isFinite(Number(score10))) return '—';
        return (Number(score10) * max / 10).toFixed(2);
    }
    function getChannels() {
        var result = platforms.map(function (c) {
            return { key: c.key, name: c.name, short: c.short, brand: c.brand, max: c.max, items: [] };
        });
        reviews.forEach(function (item) {
            var key = platformKey(item.Platform);
            var channel = result.find(function (c) { return c.key === key; });
            // Ignore all other sources in the Review Centre UI and KPIs.
            if (channel) channel.items.push(item);
        });
        return result;
    }
    function message(text, isError) {
        var node = document.getElementById('rcStatus');
        if (!node) return;
        node.hidden = false;
        node.className = isError ? 'rc-status rc-status-error' : 'rc-status';
        node.textContent = String(text == null ? '' : text).replace(/Channex/gi, 'OTA');
    }
    function hideMessage() {
        var node = document.getElementById('rcStatus');
        if (node) node.hidden = true;
    }
    // MVC JSON endpoints; CSRF token is required for reply/sync mutations.
    async function requestJson(url, data, method) {
        method = method || 'GET';
        var headers = { 'Accept': 'application/json' };
        var options = { method: method, credentials: 'same-origin', headers: headers };
        if (method !== 'GET') {
            var csrf = root.querySelector('input[name="__RequestVerificationToken"]');
            if (!csrf || !csrf.value) throw new Error('Security token missing. Reload the page.');
            headers['RequestVerificationToken'] = csrf.value;
            headers['Content-Type'] = 'application/json; charset=utf-8';
            options.body = JSON.stringify(data || {});
        }
        var response = await fetch(url, options);
        var json;
        try { json = await response.json(); }
        catch (_) { throw new Error('The server did not return valid JSON (HTTP ' + response.status + ').'); }
        if (!response.ok) throw new Error(json.message || json.Message || 'Request failed (HTTP ' + response.status + ').');
        return json;
    }

    function renderKpis() {
        // Summary cards must match the four visible platform columns.
        var visibleReviews = reviews.filter(function (r) {
            return allowedPlatforms[platformKey(r.Platform)] === true;
        });
        var total = visibleReviews.length;
        // This is the Channex PROPERTY score, not an average of four local rows.
        var officialOverall = officialScores && officialScores.OverallScore10;
        var officialCount = officialScores && officialScores.RatingCount;
        var ota = visibleReviews.filter(function (r) { return platformKey(r.Platform) !== 'website'; });
        var pending = ota.filter(function (r) { return !r.IsReplied; }).length;
        var replied = ota.filter(function (r) { return r.IsReplied; }).length;
        var rate = (pending + replied) ? percentage(replied / (pending + replied)) : null;
        var icons = {
            star: '<path d="M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z"/>',
            chat: '<path d="M4 5h16v11H8l-4 4z"/>',
            chart: '<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
            reply: '<path d="M10 8L4 13l6 5M4 13h11a5 5 0 0 1 5 5"/>',
            clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>'
        };
        function card(c, ic, label, value, unit, sub, bar, cls) {
            return '<div class="rc-kpi ' + (cls || '') + '" style="--c:' + c + '">' +
                '<div class="rc-kpi-icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + icons[ic] + '</svg></div>' +
                '<div class="rc-kpi-body"><div class="rc-kpi-label">' + label + '</div>' +
                '<div class="rc-kpi-value">' + esc(value) + (unit ? '<em>' + esc(unit) + '</em>' : '') + '</div>' +
                (bar != null ? '<div class="rc-kpi-bar"><i style="width:' + bar + '%"></i></div>' : '<div class="rc-kpi-sub">' + esc(sub) + '</div>') + '</div></div>';
        }
        document.getElementById('kpis').innerHTML =
            card('#f59e0b', 'star', 'OTA Property Rating', displayRating(officialOverall, 10), '/ 10',
                officialOverall == null ? 'Official score unavailable' :
                    (officialCount == null ? 'Overall OTA guest rating' : officialCount + ' guest ratings'), null) +
            card('#1677ff', 'chat', 'Total Reviews', total, '', 'Synced from ' + getChannels().filter(function (c) { return c.items.length; }).length + ' sources', null) +
            card('#16a34a', 'reply', 'Response Rate', rate == null ? '—' : rate + '%', '', replied + ' OTA reviews replied', rate) +
            card('#dc2626', 'clock', 'Pending Replies', pending, '', 'OTA reviews not yet replied', null, 'alert');
    }
    // Channex often combines structured fields into a single Comments string,
    // e.g. "Headline: ... Positive: ... Negative: ..." or "Title: Body: ...".
    // Split only recognised labels; keep all substantive review text.
    function reviewSections(raw) {
        var source = String(raw == null ? '' : raw).trim();
        if (!source) return [];
        var sections = [];
        var pattern = /(^|\s)(Headline|Title|Positive|Negative|Body|Pros|Cons)\s*:/gi;
        var found, lastEnd = 0, lastLabel = '';
        function add(label, text) {
            text = String(text || '').trim();
            if (!text) return;
            var lower = (label || '').toLowerCase();
            var kind = lower === 'positive' || lower === 'pros' ? 'positive'
                : lower === 'negative' || lower === 'cons' ? 'negative'
                    : lower === 'headline' || lower === 'title' ? 'title' : 'body';
            sections.push({ kind: kind, text: text });
        }
        while ((found = pattern.exec(source)) !== null) {
            // Avoid matching an inline phrase that isn't a field label.
            var prefix = source.slice(lastEnd, found.index).trim();
            if (lastLabel || prefix) add(lastLabel, prefix);
            lastLabel = found[2];
            lastEnd = pattern.lastIndex;
        }
        add(lastLabel, source.slice(lastEnd));
        return sections.length ? sections : [{ kind: 'body', text: source }];
    }
    function reviewContent(raw, id) {
        var sections = reviewSections(raw);
        if (!sections.length) return '<p class="rc-text">No written comment provided.</p>';
        var expanded = !!state.expanded[id];
        // Limit long multi-section reviews without chopping them into JSON-looking text.
        var plainLength = sections.reduce(function (n, x) { return n + x.text.length; }, 0);
        var canExpand = plainLength > 135 || sections.length > 2 || /\n/.test(String(raw || ''));
        var markup = '<div class="rc-review-content' + (expanded || !canExpand ? '' : ' rc-compact') + '">';
        sections.forEach(function (sec) {
            var lbl = sec.kind === 'positive' ? 'What went well' : sec.kind === 'negative' ? 'Could be improved' : '';
            markup += '<div class="rc-review-section" data-kind="' + sec.kind + '">' +
                (lbl ? '<span class="rc-review-label">' + lbl + '</span>' : '') +
                '<p class="rc-text">' + esc(sec.text) + '</p></div>';
        });
        markup += '</div>';
        if (canExpand) markup += '<button type="button" class="rc-more" data-act="more" data-idx="' + id + '" aria-expanded="' + (expanded ? 'true' : 'false') + '">' + (expanded ? 'Show less' : 'Read more') + '</button>';
        return markup;
    }
    function reviewMarkup(item, channel) {
        var p = scorePct(item);
        var shownScore = p === null ? 'N/A' : (p * channel.max).toFixed(1).replace(/\.0$/, '') + '/' + channel.max;
        var scoreHtml = '<span class="rc-badge" style="background:' + (p === null ? '#64748b' : ratingColor(p)) + '">' + esc(shownScore) + '</span>';
        var id = String(item._index);
        var replyTools = '';
        if (item.IsReplied) {
            replyTools = item.ReplyText
                ? '<div class="rc-reply"><b>Your reply</b>' + esc(item.ReplyText).replace(/\r?\n/g, '<br>') + '</div>'
                : '<div class="rc-reply"><b>Reply recorded</b>The OTA marks this review as replied.</div>';
        } else if (item.CanReply) {
            replyTools = '<div class="rc-meta"><span class="rc-due">' + esc(item.IsHidden ? 'Hidden' : 'Awaiting reply') + '</span>' +
                '<button type="button" class="rc-btn" data-act="open" data-idx="' + id + '">Reply</button></div>' +
                '<div class="rc-reply-box' + (state.drafts[id] != null ? ' open' : '') + '">' +
                '<textarea maxlength="4000" placeholder="Write your reply to ' + esc(item.GuestName || 'guest') + '…" data-draft="' + id + '">' + esc(state.drafts[id] || '') + '</textarea>' +
                '<div class="rc-reply-actions">' +
                '<button type="button" class="rc-btn" data-act="cancel" data-idx="' + id + '">Cancel</button>' +
                '<button type="button" class="rc-btn solid" data-act="send" data-idx="' + id + '">Send Reply</button>' +
                '</div></div>';
        } else {
            replyTools = '<div class="rc-meta"><span class="rc-due">' + esc(item.IsHidden ? 'Hidden' : 'Review received') + '</span>' +
                '<span class="rc-disabled-reply">Reply unavailable for this review</span></div>';
        }
        return '<li class="rc-item" data-idx="' + id + '">' +
            '<div class="rc-item-top"><div class="rc-avatar">' + esc(initials(item.GuestName)) + '</div>' +
            '<div class="rc-who"><b>' + esc(item.GuestName || 'Guest') + '</b><time>' + esc(prettyDate(item.ReceivedAt)) + '</time></div>' + scoreHtml + '</div>' +
            reviewContent(item.Comments, id) +
            replyTools + '</li>';
    }
    function renderBoard() {
        var savedLeft = board.scrollLeft;
        var savedTop = {};
        if (typeof board.querySelectorAll === 'function') {
            Array.prototype.forEach.call(board.querySelectorAll('.rc-col'), function (col) {
                var list = col.querySelector('.rc-list');
                if (list) savedTop[col.getAttribute('data-key')] = list.scrollTop;
            });
        }
        var channels = getChannels();
        var search = state.search.toLowerCase();
        board.innerHTML = channels.map(function (ch) {
            var items = ch.items;
            var rated = items.filter(function (r) { return scorePct(r) !== null; });
            var avg = rated.length ? rated.reduce(function (s, r) { return s + scorePct(r); }, 0) / rated.length : null;
            var pending = items.filter(function (r) { return !r.IsReplied; }).length;
            var replied = items.length - pending;
            var tab = state.tabs[ch.key] || 'not';
            var shown = items.filter(function (r) {
                var p = scorePct(r);
                var matchesScore = state.score === 'all' || (p !== null && band(p) === state.score);
                var matchesText = !search || (r.GuestName || '').toLowerCase().includes(search) ||
                    (r.Comments || '').toLowerCase().includes(search) || (r.ReplyText || '').toLowerCase().includes(search);
                return (tab === 'not' ? !r.IsReplied : r.IsReplied) && matchesScore && matchesText;
            });
            // Channex OTA aggregates are preferred. An Expedia review can be
            // synced before a property-level Expedia score becomes available.
            // In that case show an EXPLICITLY LABELLED local average, not a
            // misleading claim that the number is an official Expedia score.
            // Booking.com remains official-only to match the Channex iframe.
            // An OTA score with zero ratings must not display as 0.00/5.
            var official = ch.key === 'website' ? null : officialChannel(ch.key);
            var value = official && official.Score10 == null ? NaN : Number(official && official.Score10);
            var officialValid = official && Number.isFinite(value) && value > 0 && value <= 10 &&
                (official.RatingCount == null || Number(official.RatingCount) > 0);
            var expediaLocal = ch.key === 'expedia' && !officialValid && rated.length > 0;
            var score10 = ch.key === 'website'
                ? (avg == null ? null : avg * 10)
                : (officialValid ? value : (expediaLocal ? avg * 10 : null));
            var scoreFraction = score10 == null ? null : Math.max(0, Math.min(1, score10 / 10));
            var score = displayRating(score10, ch.max);
            var rating = scoreFraction == null ? 'No rating yet' : ratingLabel(scoreFraction);
            var ratingSource = ch.key === 'website' ? (items.length + ' direct reviews') :
                (officialValid
                    ? (official.RatingCount == null ? 'Official OTA rating' : official.RatingCount + ' guest ratings')
                    : (expediaLocal
                        ? 'Local average (' + rated.length + ' synced ' + (rated.length === 1 ? 'review' : 'reviews') + ')'
                        : 'OTA rating unavailable'));
            return '<section class="rc-col" data-key="' + esc(ch.key) + '" style="--brand:' + ch.brand + '">' +
                '<div class="rc-col-head"><div class="rc-logo">' + esc(ch.short) + '</div><h2>' + esc(ch.name) + '</h2>' +
                (pending ? '<span class="rc-pending">' + pending + ' pending</span>' : '') + '</div>' +
                '<div class="rc-score"><div class="rc-ring" style="--p:' + (scoreFraction == null ? 0 : percentage(scoreFraction)) + '"><div><b>' + esc(score) + '</b><small>/ ' + ch.max + '</small></div></div>' +
                '<div class="rc-score-info"><div class="lbl">' + esc(rating) + '</div><div class="rc-stars">' + (scoreFraction === null ? '☆☆☆☆☆' : stars(scoreFraction)) + '</div><div class="sub">' + esc(ratingSource) + '</div></div></div>' +
                '<div class="rc-stats"><div><b>' + items.length + '</b><span>Reviews</span></div>' +
                '<div><b>' + replied + '</b><span>Replied</span></div><div><b>' + pending + '</b><span>Pending</span></div></div>' +
                '<div class="rc-replies-title">Review Replies</div><div class="rc-tabs">' +
                '<button type="button" class="rc-tab' + (tab === 'not' ? ' active' : '') + '" data-act="tab" data-tab="not">Not Replied <span class="rc-count">' + pending + '</span></button>' +
                '<button type="button" class="rc-tab' + (tab === 'replied' ? ' active' : '') + '" data-act="tab" data-tab="replied">Replied <span class="rc-count">' + replied + '</span></button></div>' +
                '<ul class="rc-list">' + (shown.length ? shown.map(function (r) { return reviewMarkup(r, ch); }).join('') : '<li class="rc-empty">No reviews found</li>') + '</ul></section>';
        }).join('');
        board.scrollLeft = savedLeft;
        if (typeof board.querySelectorAll === 'function') {
            Array.prototype.forEach.call(board.querySelectorAll('.rc-col'), function (col) {
                var list = col.querySelector('.rc-list');
                var key = col.getAttribute('data-key');
                if (list && Object.prototype.hasOwnProperty.call(savedTop, key))
                    list.scrollTop = savedTop[key];
            });
        }
    }
    function render() { renderKpis(); renderBoard(); }
    async function refresh(successMessage) {
        reviews = [];
        officialScores = null;
        message('Loading your hotel reviews...', false);
        try {
            var data = await requestJson(root.dataset.dashboardUrl);
            if (!data || !Array.isArray(data.Reviews)) throw new Error('Unexpected dashboard response.');
            reviews = data.Reviews;
            officialScores = data.OfficialScores || null;
            prepareReviews();
            document.getElementById('rcHotelName').textContent = data.HotelName || 'Your property';
            render();

            // GetDashboard is deliberately just the first page (100 reviews).
            // Fetch further pages without ever sending a 2,000-review JSON body.
            // Older endpoints (without TotalReviews/PageSize) still work.
            var total = Number(data.TotalReviews);
            if (!Number.isFinite(total) || total <= 0) total = reviews.length;
            var maximum = Math.min(total, 2000);
            var pageSize = Math.max(1, Number(data.PageSize) || 100);
            var offsets = [];
            for (var offset = pageSize; offset < maximum; offset += pageSize) offsets.push(offset);

            for (var i = 0; i < offsets.length; i += 3) {
                var group = offsets.slice(i, i + 3);
                var pages = await Promise.all(group.map(function (n) {
                    return requestJson(root.dataset.pageUrl + '?offset=' + encodeURIComponent(n));
                }));
                pages.forEach(function (page) {
                    if (!page || !Array.isArray(page.Reviews)) throw new Error('Invalid review page response.');
                    page.Reviews.forEach(function (r) { reviews.push(r); });
                });
                // Preserve each review's index across filters, tabs and reply clicks.
                prepareReviews();
                render();
                if (reviews.length < maximum)
                    message('Loading reviews: ' + reviews.length + ' of ' + maximum + '...', false);
            }
            if (data.Truncated)
                message('Showing the 2,000 most recent reviews. Older reviews are not loaded.', false);
            else if (successMessage)
                message(successMessage, false);
            else
                hideMessage();
        } catch (error) {
            if (reviews.length)
                message('Some reviews could not be loaded (' + reviews.length + ' shown). ' +
                    error.message + ' Refresh to retry.', true);
            else
                message('Unable to load reviews: ' + error.message, true);
            console.error('ReviewCentre GetDashboard:', error);
        }
    }
    async function sendReply(index, button) {
        if (sending) return;
        var item = reviews[index];
        if (!item || !item.CanReply || !item.ReviewId) return;
        var row = board.querySelector('.rc-item[data-idx="' + index + '"]');
        var text = row && row.querySelector('textarea');
        var replyText = text ? text.value.trim() : '';
        if (!replyText || replyText.length > 4000) { message('Please enter a reply (maximum 4,000 characters).', true); if (text) text.focus(); return; }
        sending = true;
        button.disabled = true;
        button.textContent = 'Sending...';
        try {
            // EXISTING endpoint used by FrontDeskCalender PageMethods.ReplyToOtaReview.
            // No change to the Channex iframe, channel setup or review repository.
            var reply = await requestJson(root.dataset.replyUrl, {
                ReviewId: item.ReviewId,
                ReplyText: replyText
            }, 'POST');
            if (!reply || reply.ok !== true) throw new Error(reply && reply.message ? reply.message : 'The OTA platform did not accept the reply.');
            delete state.drafts[index];
            item.IsReplied = true;
            item.CanReply = false;
            item.ReplyText = replyText;
            render();
            message(reply.message || 'Reply sent successfully.', false);
        } catch (error) {
            message('Reply failed: ' + error.message, true);
            console.error('ReviewCentre Reply:', error);
            button.disabled = false;
            button.textContent = 'Send Reply';
        } finally { sending = false; }
    }

    board.addEventListener('click', function (event) {
        var button = event.target.closest('[data-act]');
        if (!button) return;
        // ASP.NET WebForms renders a single large form. These controls must NEVER
        // post back or replace the entire review board for local UI actions.
        if (event.preventDefault) event.preventDefault();
        if (event.stopPropagation) event.stopPropagation();
        var act = button.getAttribute('data-act');
        if (act === 'tab') {
            var col = button.closest('.rc-col');
            if (!col) return;
            state.tabs[col.getAttribute('data-key')] = button.getAttribute('data-tab');
            renderBoard();
            return;
        }
        var idx = Number(button.getAttribute('data-idx'));
        var row = button.closest('.rc-item');
        if (!row || !reviews[idx]) return;
        if (act === 'more') {
            var content = row.querySelector('.rc-review-content');
            if (!content) return;
            state.expanded[idx] = !state.expanded[idx];
            content.classList.toggle('rc-compact', !state.expanded[idx]);
            button.textContent = state.expanded[idx] ? 'Show less' : 'Read more';
            button.setAttribute('aria-expanded', state.expanded[idx] ? 'true' : 'false');
            return;
        }
        if (act === 'open') {
            var box = row.querySelector('.rc-reply-box');
            if (!box) return;
            if (state.drafts[idx] == null) state.drafts[idx] = '';
            box.classList.add('open');
            var input = box.querySelector('textarea');
            if (input && input.focus) input.focus();
            return;
        }
        if (act === 'cancel') {
            var cancelBox = row.querySelector('.rc-reply-box');
            if (cancelBox) {
                cancelBox.classList.remove('open');
                var draft = cancelBox.querySelector('textarea');
                if (draft) draft.value = '';
            }
            delete state.drafts[idx];
            return;
        }
        if (act === 'send') sendReply(idx, button);
    });
    board.addEventListener('input', function (event) {
        if (!event.target.matches('textarea[data-draft]')) return;
        state.drafts[event.target.getAttribute('data-draft')] = event.target.value;
    });
    document.getElementById('search').addEventListener('input', function (event) {
        state.search = event.target.value.trim(); renderBoard();
    });
    document.getElementById('scoreFilter').addEventListener('change', function (event) {
        state.score = event.target.value; renderBoard();
    });
    var syncButton = document.getElementById('rcSyncReviews');
    if (syncButton) {
        syncButton.addEventListener('click', async function () {
            if (syncButton.disabled) return;
            syncButton.disabled = true;
            syncButton.textContent = 'Syncing…';
            message('Syncing reviews for this hotel from the connected channels...', false);
            try {
                var result = await requestJson(root.dataset.syncUrl, {}, 'POST');
                if (!result || result.ok !== true)
                    throw new Error(result && result.message ? result.message : 'Review sync failed.');
                await refresh(result.message || 'Reviews synchronized.');
            } catch (error) {
                message('Sync failed: ' + error.message, true);
                console.error('ReviewCentre Sync:', error);
            } finally {
                syncButton.disabled = false;
                syncButton.textContent = 'Sync Reviews';
            }
        });
    }
    refresh();
})();

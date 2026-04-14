
let registeredMediaButtons = [];
let galleryPreviewModalElem = null;

/** Registers a media button for extensions. 'mediaTypes' filters by type eg ['audio'], null means all. 'isDefault' promotes to visible (vs More dropdown). 'showInHistory' controls whether button appears in the History panel. */
function registerMediaButton(name, action, title = '', mediaTypes = null, isDefault = false, showInHistory = true, href = null, is_download = false) {
    registeredMediaButtons.push({ name, action, title, mediaTypes, isDefault, showInHistory, href, is_download });
}

function isIndexedHistorySrc(src) {
    return src && (src.startsWith('OutputIndex/') || src.startsWith('/OutputIndex/'));
}

function parseOutputMetadata(metadata) {
    if (!metadata) {
        return null;
    }
    let readable = metadata;
    try {
        readable = interpretMetadata(metadata) || metadata;
        return JSON.parse(readable);
    }
    catch (e) {
        console.log(`Failed to parse output metadata: ${e}`);
        return null;
    }
}

function formatOutputMetadata(metadata) {
    let formatted = formatMetadata(metadata);
    if (formatted) {
        return formatted;
    }
    let parsed = parseOutputMetadata(metadata);
    if (!parsed) {
        return '';
    }
    let raw = [];
    for (let [key, value] of Object.entries(parsed)) {
        if (value == null || value === '' || typeof value === 'object') {
            continue;
        }
        raw.push(`<span class="param_view_block tag-text"><span class="param_view_name">${escapeHtml(key)}</span>: <span class="param_view tag-text-soft">${escapeHtml(`${value}`)}</span></span>`);
    }
    return raw.join(', ');
}

function downloadTextFile(filename, content, mimeType = 'application/json') {
    let blob = new Blob([content], { type: mimeType });
    let url = URL.createObjectURL(blob);
    let link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function ensureGalleryPreviewModal() {
    if (galleryPreviewModalElem) {
        return galleryPreviewModalElem;
    }
    let modal = document.createElement('div');
    modal.id = 'gallery_preview_modal';
    modal.className = 'gallery-preview-modal';
    modal.innerHTML = `
        <button type="button" class="gallery-preview-close" aria-label="Close">X</button>
        <div class="gallery-preview-stage">
            <img class="gallery-preview-image" alt="Gallery preview">
        </div>`;
    modal.addEventListener('click', (e) => {
        if (e.target === modal || findParentOfClass(e.target, 'gallery-preview-close')) {
            closeGalleryPreviewModal();
        }
    });
    document.body.appendChild(modal);
    galleryPreviewModalElem = modal;
    return modal;
}

function closeGalleryPreviewModal() {
    let modal = ensureGalleryPreviewModal();
    modal.classList.remove('gallery-preview-open');
    let image = modal.querySelector('.gallery-preview-image');
    if (image) {
        image.removeAttribute('src');
    }
}

function openGalleryPreviewModal(src) {
    if (!src) {
        return;
    }
    let modal = ensureGalleryPreviewModal();
    let image = modal.querySelector('.gallery-preview-image');
    image.src = src;
    modal.classList.add('gallery-preview-open');
}

window.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && galleryPreviewModalElem?.classList.contains('gallery-preview-open')) {
        closeGalleryPreviewModal();
    }
});

function readOutputBrowserSettings(storagePrefix, browser) {
    let sortBy = localStorage.getItem(`${storagePrefix}_sort_by`) ?? 'Name';
    let reverse = localStorage.getItem(`${storagePrefix}_sort_reverse`) == 'true';
    let allowAnims = localStorage.getItem(`${storagePrefix}_allow_anims`) != 'false';
    let sortElem = document.getElementById(`${storagePrefix}_sort_by`);
    let sortReverseElem = document.getElementById(`${storagePrefix}_sort_reverse`);
    let allowAnimsElem = document.getElementById(`${storagePrefix}_allow_anims`);
    let fix = null;
    if (sortElem) {
        sortBy = sortElem.value;
        reverse = sortReverseElem.checked;
        allowAnims = allowAnimsElem.checked;
    }
    else { // first call happens before headers are built atm
        fix = () => {
            let sortElem = document.getElementById(`${storagePrefix}_sort_by`);
            let sortReverseElem = document.getElementById(`${storagePrefix}_sort_reverse`);
            let allowAnimsElem = document.getElementById(`${storagePrefix}_allow_anims`);
            sortElem.value = sortBy;
            sortReverseElem.checked = reverse;
            sortElem.addEventListener('change', () => {
                localStorage.setItem(`${storagePrefix}_sort_by`, sortElem.value);
                browser.lightRefresh();
            });
            sortReverseElem.addEventListener('change', () => {
                localStorage.setItem(`${storagePrefix}_sort_reverse`, sortReverseElem.checked);
                browser.lightRefresh();
            });
            allowAnimsElem.addEventListener('change', () => {
                localStorage.setItem(`${storagePrefix}_allow_anims`, allowAnimsElem.checked);
                browser.lightRefresh();
            });
        }
    }
    return { sortBy, reverse, allowAnims, fix };
}

function listOutputFolderAndFilesForBrowser(path, isRefresh, callback, depth, storagePrefix, browser) {
    let { sortBy, reverse, fix } = readOutputBrowserSettings(storagePrefix, browser);
    let prefix = path == '' ? '' : (path.endsWith('/') ? path : `${path}/`);
    genericRequest('ListIndexedImages', {'path': path, 'depth': depth, 'sortBy': sortBy, 'sortReverse': reverse}, data => {
        let folders = data.folders.sort((a, b) => b.toLowerCase().localeCompare(a.toLowerCase()));
        function isPreSortFile(f) {
            return f.src == 'index.html'; // Grid index files
        }
        let preFiles = data.files.filter(f => isPreSortFile(f));
        let postFiles = data.files.filter(f => !isPreSortFile(f));
        data.files = preFiles.concat(postFiles);
        let mapped = data.files.map(f => {
            let fullSrc = `${prefix}${f.src}`;
            return {
                'name': fullSrc,
                'data': {
                    'src': f.url ?? `${getImageOutPrefix()}/${fullSrc}`,
                    'fullsrc': fullSrc,
                    'name': f.src,
                    'metadata': f.metadata ?? null,
                    'entry_id': f.entry_id ?? null
                }
            };
        });
        callback(folders, mapped);
        if (fix) {
            fix();
        }
    });
}

function listOutputGalleryFolderAndFiles(path, isRefresh, callback, depth) {
    let sortElem = document.getElementById('image_gallery_sort_by');
    let sortReverseElem = document.getElementById('image_gallery_sort_reverse');
    let sortBy = sortElem ? sortElem.value : (localStorage.getItem('image_gallery_sort_by') ?? 'Date');
    let sortReverse = sortReverseElem ? sortReverseElem.checked : (localStorage.getItem('image_gallery_sort_reverse') == 'true');
    let prefix = path == '' ? '' : (path.endsWith('/') ? path : `${path}/`);
    genericRequest('ListIndexedImages', {'path': path, 'depth': depth, 'sortBy': sortBy, 'sortReverse': sortReverse}, data => {
        let mapped = data.files.map(f => {
            let fullSrc = `${prefix}${f.src}`;
            return {
                'name': fullSrc,
                'data': {
                    'src': f.url ?? `${getImageOutPrefix()}/${fullSrc}`,
                    'fullsrc': fullSrc,
                    'name': f.src,
                    'metadata': f.metadata ?? null,
                    'entry_id': f.entry_id ?? null
                }
            };
        });
        callback(data.folders, mapped);
        // 정렬 컨트롤이 아직 DOM에 없으면(첫 호출) 생성 후 이벤트 연결
        if (!sortElem) {
            let newSortElem = document.getElementById('image_gallery_sort_by');
            let newSortReverseElem = document.getElementById('image_gallery_sort_reverse');
            if (newSortElem) {
                newSortElem.value = sortBy;
                newSortReverseElem.checked = sortReverse;
                newSortElem.addEventListener('change', () => {
                    localStorage.setItem('image_gallery_sort_by', newSortElem.value);
                    imageGalleryBrowser.lightRefresh();
                });
                newSortReverseElem.addEventListener('change', () => {
                    localStorage.setItem('image_gallery_sort_reverse', newSortReverseElem.checked);
                    imageGalleryBrowser.lightRefresh();
                });
            }
        }
    });
}

/**
 * Gallery 브라우저 캐시를 비우고 루트부터 다시 읽는다.
 */
function refreshImageGalleryBrowser() {
    if (typeof imageGalleryBrowser === 'undefined') {
        return;
    }
    imageGalleryBrowser.lastListCache = null;
    imageGalleryBrowser.navigate('');
}

let imageGalleryRefreshTimeout = null;

function scheduleImageGalleryRefresh() {
    if (typeof imageGalleryBrowser === 'undefined') {
        return;
    }
    if (imageGalleryRefreshTimeout) {
        clearTimeout(imageGalleryRefreshTimeout);
    }
    imageGalleryRefreshTimeout = setTimeout(() => {
        imageGalleryRefreshTimeout = null;
        refreshImageGalleryBrowser();
    }, 200);
}

let gallerySelectionMode = false;
let gallerySelectedEntries = new Set();

function gallerySetSelectionMode(isEnabled) {
    gallerySelectionMode = isEnabled;
    if (!isEnabled) {
        gallerySelectedEntries.clear();
    }
    refreshImageGallerySelectionUi();
}

function galleryToggleSelectionMode() {
    gallerySetSelectionMode(!gallerySelectionMode);
}

function galleryToggleEntrySelection(entryId, isSelected) {
    if (!entryId) {
        return;
    }
    if (isSelected) {
        gallerySelectedEntries.add(entryId);
    }
    else {
        gallerySelectedEntries.delete(entryId);
    }
    refreshImageGallerySelectionUi();
}

function galleryDeleteSelected() {
    if (gallerySelectedEntries.size === 0) {
        return;
    }
    if (!uiImprover.lastShift && getUserSetting('ui.checkifsurebeforedelete', true) && !confirm(`선택한 ${gallerySelectedEntries.size}개 항목을 갤러리에서 삭제할까요?`)) {
        return;
    }
    genericRequest('DeleteIndexedImages', { 'entry_ids': [...gallerySelectedEntries] }, data => {
        if (data.error) {
            doError(data.error);
            return;
        }
        let gallerySection = getRequiredElementById('imagegallerybrowser-content');
        for (let entryId of [...gallerySelectedEntries]) {
            let div = gallerySection.querySelector(`[data-entry-id="${entryId}"]`);
            if (div) {
                div.remove();
            }
        }
        gallerySelectedEntries.clear();
        refreshImageGallerySelectionUi();
        imageGalleryBrowser.lightRefresh();
    });
}

function refreshImageGallerySelectionUi() {
    let toggleButton = document.getElementById('image_gallery_select_toggle');
    let deleteButton = document.getElementById('image_gallery_delete_selected');
    if (toggleButton) {
        toggleButton.innerText = gallerySelectionMode ? '선택 종료' : '선택';
    }
    if (deleteButton) {
        deleteButton.disabled = gallerySelectedEntries.size === 0;
        deleteButton.innerText = gallerySelectedEntries.size > 0 ? `선택 삭제 (${gallerySelectedEntries.size})` : '선택 삭제';
    }
    let content = document.getElementById('imagegallerybrowser-content');
    if (!content) {
        return;
    }
    for (let div of content.querySelectorAll('[data-entry-id]')) {
        let entryId = div.dataset.entryId;
        let existing = div.querySelector('.gallery-select-checkbox');
        if (!gallerySelectionMode) {
            existing?.remove();
            div.classList.remove('gallery-selection-enabled');
            continue;
        }
        div.classList.add('gallery-selection-enabled');
        if (!existing) {
            let checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.className = 'gallery-select-checkbox';
            checkbox.title = '선택';
            checkbox.addEventListener('mousedown', (e) => {
                e.stopPropagation();
            });
            checkbox.addEventListener('click', (e) => {
                e.stopPropagation();
            });
            checkbox.addEventListener('change', (e) => {
                e.stopPropagation();
                galleryToggleEntrySelection(entryId, checkbox.checked);
            });
            div.prepend(checkbox);
            existing = checkbox;
        }
        existing.checked = gallerySelectedEntries.has(entryId);
    }
}

function buttonsForImage(fullsrc, src, metadata, entryId = null) {
    let isDataImage = src.startsWith('data:');
    let isIndexed = isIndexedHistorySrc(src);
    let mediaType = getMediaType(src);
    let parsedMetadata = parseOutputMetadata(metadata) || {};
    let buttons = [];
    if (permissions.hasPermission('user_star_images') && !isDataImage && !isIndexed) {
        buttons.push({
            label: parsedMetadata.is_starred ? 'Unstar' : 'Star',
            title: 'Star or unstar this image - starred images get moved to a separate folder and highlighted.',
            className: parsedMetadata.is_starred ? ' star-button button-starred-image' : ' star-button',
            onclick: (e) => {
                toggleStar(fullsrc, src);
            }
        });
    }
    if (metadata) {
        buttons.push({
            label: 'Copy Raw Metadata',
            title: `Copies the raw form of the image's metadata to your clipboard (usually JSON text).`,
            onclick: (e) => {
                copyText(metadata);
                doNoticePopover('Copied!', 'notice-pop-green');
            }
        });
        buttons.push({
            label: 'Download Metadata',
            title: `Downloads the raw metadata of this image as a JSON text file.`,
            href: `data:application/json;charset=utf-8,${encodeURIComponent(metadata)}`,
            is_download: true
        });
    }
    if (!isDataImage) {
        buttons.push({
            label: 'Copy Path',
            title: 'Copies the relative file path of this image to your clipboard.',
            onclick: (e) => {
                copyText(fullsrc);
                doNoticePopover('Copied!', 'notice-pop-green');
            }
        });
    }
    if (permissions.hasPermission('local_image_folder') && !isDataImage && !isIndexed) {
        buttons.push({
            label: 'Open In Folder',
            title: 'Opens the folder containing this image in your local PC file explorer.',
            onclick: (e) => {
                genericRequest('OpenImageFolder', {'path': fullsrc}, data => {});
            }
        });
    }
    buttons.push({
        label: 'Download',
        title: 'Downloads this image to your PC.',
        href: escapeHtmlForUrl(src),
        is_download: true
    });
    if (parsedMetadata.source == 'comfy_workflow' && parsedMetadata.workflow_api) {
        buttons.push({
            label: '워크플로우 내보내기',
            title: '이 결과를 만든 Comfy Workflow API JSON을 다운로드합니다.',
            onclick: () => {
                let safeName = (fullsrc.split('/').pop() || 'workflow').replace(/\.[^.]+$/, '');
                downloadTextFile(`${safeName}.workflow_api.json`, JSON.stringify(JSON.parse(parsedMetadata.workflow_api), null, 2));
            }
        });
    }
    if (permissions.hasPermission('user_delete_image') && !isDataImage && !isIndexed) {
        buttons.push({
            label: 'Delete',
            title: 'Deletes this image from the server.',
            onclick: (e) => {
                if (!uiImprover.lastShift && getUserSetting('ui.checkifsurebeforedelete', true) && !confirm('Are you sure you want to delete this image?\nHold shift to bypass.')) {
                    return;
                }
                let deleteBehavior = getUserSetting('ui.deleteimagebehavior', 'next');
                let shifted = deleteBehavior == 'nothing' ? false : shiftToNextImagePreview(deleteBehavior == 'next', imageFullView.isOpen());
                if (!shifted) {
                    imageFullView.close();
                }
                genericRequest('DeleteImage', {'path': fullsrc}, data => {
                    if (e) {
                        e.remove();
                    }
                    let gallerySection = getRequiredElementById('imagegallerybrowser-content');
                    let div = gallerySection.querySelector(`.image-block[data-name="${fullsrc}"]`);
                    if (div) {
                        div.remove();
                    }
                    div = gallerySection.querySelector(`.image-block[data-name="${src}"]`);
                    if (div) {
                        div.remove();
                    }
                    let currentImage = currentImageHelper.getCurrentImage();
                    if (currentImage && currentImage.dataset.src == src) {
                        setCurrentImage(null);
                    }
                    div = getRequiredElementById('current_image_batch').querySelector(`.image-block[data-src="${src}"]`);
                    if (div) {
                        removeImageBlockFromBatch(div);
                    }
                });
            }
        });
    }
    if (permissions.hasPermission('user_delete_image') && isIndexed && entryId) {
        buttons.push({
            label: 'Delete',
            title: '갤러리에서 이 항목과 파일을 삭제합니다.',
            onclick: (e) => {
                if (!uiImprover.lastShift && getUserSetting('ui.checkifsurebeforedelete', true) && !confirm('갤러리에서 이 이미지를 삭제할까요?\nShift를 누른 채로 클릭하면 확인 창을 건너뜁니다.')) {
                    return;
                }
                let deleteBehavior = getUserSetting('ui.deleteimagebehavior', 'next');
                let shifted = deleteBehavior == 'nothing' ? false : shiftToNextImagePreview(deleteBehavior == 'next', imageFullView.isOpen());
                if (!shifted) {
                    imageFullView.close();
                }
                genericRequest('DeleteIndexedImage', {'entry_id': entryId}, data => {
                    if (data.error) {
                        doError(data.error);
                        return;
                    }
                    if (e) {
                        e.remove();
                    }
                    let gallerySection = getRequiredElementById('imagegallerybrowser-content');
                    let div = gallerySection.querySelector(`.image-block[data-name="${fullsrc}"]`);
                    if (div) {
                        div.remove();
                    }
                    let currentImage = currentImageHelper.getCurrentImage();
                    if (currentImage && currentImage.dataset.src == src) {
                        setCurrentImage(null);
                    }
                });
            }
        });
    }
    for (let reg of registeredMediaButtons) {
        if (reg.showInHistory && (!reg.mediaTypes || reg.mediaTypes.includes(mediaType))) {
            buttons.push({
                label: reg.name,
                title: reg.title,
                href: reg.href,
                is_download: reg.is_download,
                onclick: () => reg.action(src)
            });
        }
    }
    return buttons;
}

function buildOutputFileDescription(image, storagePrefix) {
    let buttons = buttonsForImage(image.data.fullsrc, image.data.src, image.data.metadata, image.data.entry_id ?? null);
    let parsedMeta = { is_starred: false };
    if (image.data.metadata) {
        parsedMeta = parseOutputMetadata(image.data.metadata) || parsedMeta;
    }
    let formattedMetadata = formatOutputMetadata(image.data.metadata);
    let description = image.data.name + "\n" + formattedMetadata;
    let name = image.data.name;
    let allowAnims = localStorage.getItem(`${storagePrefix}_allow_anims`) != 'false';
    let allowAnimToggle = allowAnims ? '' : '&noanim=true';
    let forceImage = null, forcePreview = null;
    let extension = image.data.name.split('.').pop();
    if (extension == 'html') {
        forceImage = 'imgs/html.jpg';
        forcePreview = forceImage;
    }
    else if (['wav', 'mp3', 'aac', 'ogg', 'flac'].includes(extension)) {
        forcePreview = 'imgs/audio_placeholder.jpg';
    }
    let dragImage = forceImage ?? `${image.data.src}`;
    let imageSrc = forcePreview ?? `${image.data.src}?preview=true${allowAnimToggle}`;
    let searchable = `${image.data.name}, ${image.data.metadata}, ${image.data.fullsrc}`;
    let detail_list = [escapeHtml(image.data.name), formattedMetadata.replaceAll('<br>', '&emsp;')];
    let aspectRatio = parsedMeta.sui_image_params?.width && parsedMeta.sui_image_params?.height ? parsedMeta.sui_image_params.width / parsedMeta.sui_image_params.height : null;
    return {
        name,
        description,
        buttons,
        'image': imageSrc,
        'dragimage': dragImage,
        className: parsedMeta.is_starred ? 'image-block-starred' : '',
        searchable,
        display: name,
        detail_list,
        aspectRatio,
        entryId: image.data.entry_id ?? null,
        src: image.data.src,
        fullSrc: image.data.fullsrc,
        metadataRaw: image.data.metadata ?? null
    };
}

function getGalleryViewerSrc(image, storagePrefix = 'image_gallery') {
    if (!image?.data?.src) {
        return '';
    }
    let src = image.data.src;
    let extension = (image.data.name || '').split('.').pop()?.toLowerCase();
    if (!isIndexedHistorySrc(src) || extension == 'html' || ['wav', 'mp3', 'aac', 'ogg', 'flac'].includes(extension) || isVideoExt(src) || isAudioExt(src)) {
        return src;
    }
    let allowAnims = localStorage.getItem(`${storagePrefix}_allow_anims`) != 'false';
    let allowAnimToggle = allowAnims ? '' : '&noanim=true';
    let separator = src.includes('?') ? '&' : '?';
    return `${src}${separator}preview=true${allowAnimToggle}`;
}

function selectOutputInHistory(image, div) {
    if (gallerySelectionMode && div?.closest('#imagegallerybrowser-content')) {
        let checkbox = div.querySelector('.gallery-select-checkbox');
        if (checkbox) {
            checkbox.checked = !checkbox.checked;
            galleryToggleEntrySelection(div.dataset.entryId, checkbox.checked);
        }
        return;
    }
    lastHistoryImage = image.data.src;
    lastHistoryImageDiv = div;
    if (div?.closest('#imagegallerybrowser-content')) {
        openGalleryPreviewModal(getGalleryViewerSrc(image));
        return;
    }
    if (image.data.name.endsWith('.html')) {
        window.open(image.data.src, '_blank');
    }
    else {
        if (!div.dataset.metadata) {
            div.dataset.metadata = image.data.metadata;
            div.dataset.src = image.data.src;
        }
        setCurrentImage(image.data.src, div.dataset.metadata, 'history', false, false, false, false, image.data.fullsrc, image.data.entry_id ?? null);
    }
}

let imageGalleryBrowser = new GenPageBrowserClass('image_gallery', listOutputGalleryFolderAndFiles, 'imagegallerybrowser', 'Big Cards', (image) => buildOutputFileDescription(image, 'image_gallery'), selectOutputInHistory,
    `<label for="image_gallery_sort_by">정렬:</label> <select id="image_gallery_sort_by"><option value="Date">날짜</option><option value="Name">이름</option></select> <input type="checkbox" id="image_gallery_sort_reverse" autocomplete="off"> <label for="image_gallery_sort_reverse">역순</label> <button id="image_gallery_select_toggle" class="basic-button" onclick="galleryToggleSelectionMode()">선택</button> <button id="image_gallery_delete_selected" class="basic-button" onclick="galleryDeleteSelected()" disabled>선택 삭제</button>`);
imageGalleryBrowser.showDisplayFormat = false;
imageGalleryBrowser.showDepth = false;
imageGalleryBrowser.showUpFolder = false;
imageGalleryBrowser.showFilter = false;
imageGalleryBrowser.builtEvent = () => {
    if (imageGalleryBrowser.folderTreeDiv) {
        imageGalleryBrowser.folderTreeDiv.style.display = 'none';
    }
    let splitter = document.getElementById(`${imageGalleryBrowser.id}-splitter`);
    if (splitter) {
        splitter.style.display = 'none';
    }
    if (imageGalleryBrowser.fullContentDiv) {
        imageGalleryBrowser.fullContentDiv.style.width = '100%';
    }
    refreshImageGallerySelectionUi();
};

function storeImageToHistoryWithCurrentParams(img) {
    let data = getGenInput();
    data['image'] = img;
    delete data['initimage'];
    delete data['maskimage'];
    genericRequest('AddImageToHistory', data, res => {
        mainGenHandler.gotImageResult(res.images[0].image, res.images[0].metadata, '0');
    });
}

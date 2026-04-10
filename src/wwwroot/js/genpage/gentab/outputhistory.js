
let registeredMediaButtons = [];

/** Registers a media button for extensions. 'mediaTypes' filters by type eg ['audio'], null means all. 'isDefault' promotes to visible (vs More dropdown). 'showInHistory' controls whether button appears in the History panel. */
function registerMediaButton(name, action, title = '', mediaTypes = null, isDefault = false, showInHistory = true, href = null, is_download = false) {
    registeredMediaButtons.push({ name, action, title, mediaTypes, isDefault, showInHistory, href, is_download });
}

function isIndexedHistorySrc(src) {
    return src && (src.startsWith('OutputIndex/') || src.startsWith('/OutputIndex/'));
}

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
                    'metadata': interpretMetadata(f.metadata),
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
    let prefix = path == '' ? '' : (path.endsWith('/') ? path : `${path}/`);
    genericRequest('ListIndexedImages', {'path': path, 'depth': depth, 'sortBy': 'Date', 'sortReverse': false}, data => {
        let mapped = data.files.map(f => {
            let fullSrc = `${prefix}${f.src}`;
            return {
                'name': fullSrc,
                'data': {
                    'src': f.url ?? `${getImageOutPrefix()}/${fullSrc}`,
                    'fullsrc': fullSrc,
                    'name': f.src,
                    'metadata': interpretMetadata(f.metadata),
                    'entry_id': f.entry_id ?? null
                }
            };
        });
        mapped.reverse();
        callback(data.folders, mapped);
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

function buttonsForImage(fullsrc, src, metadata) {
    let isDataImage = src.startsWith('data:');
    let isIndexed = isIndexedHistorySrc(src);
    let mediaType = getMediaType(src);
    let buttons = [];
    if (permissions.hasPermission('user_star_images') && !isDataImage && !isIndexed) {
        buttons.push({
            label: (metadata && JSON.parse(metadata).is_starred) ? 'Unstar' : 'Star',
            title: 'Star or unstar this image - starred images get moved to a separate folder and highlighted.',
            className: (metadata && JSON.parse(metadata).is_starred) ? ' star-button button-starred-image' : ' star-button',
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
    let buttons = buttonsForImage(image.data.fullsrc, image.data.src, image.data.metadata);
    let parsedMeta = { is_starred: false };
    if (image.data.metadata) {
        let metadata = image.data.metadata;
        try {
            metadata = interpretMetadata(image.data.metadata);
            parsedMeta = JSON.parse(metadata) || parsedMeta;
        }
        catch (e) {
            console.log(`Failed to parse image metadata: ${e}, metadata was ${metadata}`);
        }
    }
    let formattedMetadata = formatMetadata(image.data.metadata);
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
    return { name, description, buttons, 'image': imageSrc, 'dragimage': dragImage, className: parsedMeta.is_starred ? 'image-block-starred' : '', searchable, display: name, detail_list, aspectRatio };
}

function selectOutputInHistory(image, div) {
    lastHistoryImage = image.data.src;
    lastHistoryImageDiv = div;
    let curImg = currentImageHelper.getCurrentImage();
    if (curImg && curImg.dataset.src == image.data.src) {
        curImg.dataset.batch_id = 'history';
        curImg.click();
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
        setCurrentImage(image.data.src, div.dataset.metadata, 'history');
    }
}

let imageGalleryBrowser = new GenPageBrowserClass('image_gallery', listOutputGalleryFolderAndFiles, 'imagegallerybrowser', 'Big Cards', (image) => buildOutputFileDescription(image, 'image_gallery'), selectOutputInHistory, '');
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

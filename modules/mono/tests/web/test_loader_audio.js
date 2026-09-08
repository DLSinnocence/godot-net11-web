/* global __dirname, require */

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const root = path.resolve(__dirname, '../../../..');

function deferred() {
	let resolve;
	let reject;
	const promise = new Promise((promiseResolve, promiseReject) => {
		resolve = promiseResolve;
		reject = promiseReject;
	});
	return { promise, reject, resolve };
}

async function flushPromises() {
	await Promise.resolve();
	await Promise.resolve();
	await Promise.resolve();
	await Promise.resolve();
	await Promise.resolve();
}

function loadMonoBridge() {
	const filename = path.join(root, 'modules/mono/web/mono_bridge.js');
	const source = fs.readFileSync(filename, 'utf8')
		.replace(
			'const dotnetjs = await import(\'./_framework/dotnet.js\');',
			'const dotnetjs = globalThis.__dotnetjs;'
		)
		.concat('\nglobalThis.__Godot = Godot;\n');
	let resourceLoader = null;
	const dotnet = {
		create: () => Promise.resolve({
			getAssemblyExports: () => {},
			getConfig: () => ({ mainAssemblyName: 'Test.dll' }),
			Module: {},
			runMain: () => {},
			setModuleImports: () => {},
		}),
		download: async () => {},
		withConfig() {
			return this;
		},
		withModuleConfig() {
			return this;
		},
		withResourceLoader(loader) {
			resourceLoader = loader;
			return this;
		},
	};
	const context = vm.createContext({
		__dotnetjs: { dotnet },
		Promise,
	});
	vm.runInContext(source, context, { filename });
	return {
		getResourceLoader: () => resourceLoader,
		Godot: context.__Godot,
	};
}

test('the .NET resource loader returns the default URI for unhandled resources', async () => {
	const bridge = loadMonoBridge();
	await bridge.Godot({
		emscriptenPoolSize: 0,
		getPreloadedWasm: () => null,
		locateFile: () => 'dotnet.native.wasm',
	});
	const defaultUri = 'https://example.test/_framework/System.Private.CoreLib.wasm';
	assert.equal(bridge.getResourceLoader()('wasm', 'System.Private.CoreLib.wasm', defaultUri), defaultUri);
});

function createAudioHarness() {
	const filename = path.join(root, 'platform/web/js/libs/library_godot_audio.js');
	const source = fs.readFileSync(filename, 'utf8')
		.concat(`
			Object.assign(GodotAudioScript, GodotAudioScript.$GodotAudioScript);
			Object.assign(GodotAudioWorklet, GodotAudioWorklet.$GodotAudioWorklet);
			globalThis.__audioExports = {
				GodotAudio: _GodotAudio.$GodotAudio,
				GodotAudioScript,
				GodotAudioWorklet,
				SampleNode,
			};
		`);
	const contexts = [];
	const workletNodes = [];
	const finishedSamples = [];
	const intervals = new Map();
	let intervalId = 0;

	class MockAudioWorkletNode {
		constructor(ctx, name, options) {
			this.context = ctx;
			this.name = name;
			this.options = options;
			this.connections = [];
			this.disconnectCount = 0;
			this.messages = [];
			this.parameters = new Map([
				['reset', { setValueAtTime() {} }],
			]);
			this.port = {
				onmessage: null,
				postMessage: (message) => this.messages.push(message),
			};
			workletNodes.push(this);
		}

		connect(destination) {
			this.connections.push(destination);
		}

		disconnect() {
			this.disconnectCount++;
		}
	}

	function AudioContext() {
		return contexts.shift();
	}

	const context = vm.createContext({
		AudioWorkletNode: MockAudioWorkletNode,
		clearInterval: (id) => intervals.delete(id),
		console,
		Float32Array,
		HEAPF32: new Float32Array(128),
		LibraryManager: { library: {} },
		Map,
		mergeInto() {},
		navigator: {},
		Promise,
		setInterval: (callback) => {
			const id = ++intervalId;
			intervals.set(id, callback);
			return id;
		},
		window: { AudioContext },
		autoAddDeps() {},
	});
	vm.runInContext(source, context, { filename });
	Object.assign(context, context.__audioExports, {
		GodotConfig: {
			locate_file: (name) => name,
		},
		GodotOS: {
			atexit() {},
		},
		GodotRuntime: {
			allocString: (value) => value,
			error() {},
			free() {},
			heapSlice: () => new Float32Array(),
			heapSub: () => new Float32Array(),
		},
	});
	context.GodotAudio.sampleFinishedCallback = (id) => finishedSamples.push(id);
	context.GodotAudio.SampleNodeBus = {
		create: () => ({
			clearCount: 0,
			clear() {
				this.clearCount++;
			},
			getInputNode: () => ({}),
			setVolume() {},
		}),
	};

	function makeContext(modules = {}) {
		const scriptNodes = [];
		const sources = [];
		const audioContext = {
			audioWorklet: {
				addModule: (name) => modules[name]?.promise ?? Promise.resolve(),
			},
			baseLatency: 0,
			closeCount: 0,
			createBufferSource() {
				const bufferSource = {
					connections: [],
					disconnectCount: 0,
					listeners: new Map(),
					playbackRate: { value: 1 },
					startCount: 0,
					stopCount: 0,
					addEventListener(type, listener) {
						this.listeners.set(type, listener);
					},
					removeEventListener(type, listener) {
						if (this.listeners.get(type) === listener) {
							this.listeners.delete(type);
						}
					},
					connect(destination) {
						this.connections.push(destination);
					},
					disconnect() {
						this.disconnectCount++;
					},
					start() {
						this.startCount++;
					},
					stop() {
						this.stopCount++;
					},
				};
				sources.push(bufferSource);
				return bufferSource;
			},
			mediaSourceCount: 0,
			createMediaStreamSource() {
				this.mediaSourceCount++;
				return { disconnect() {} };
			},
			createScriptProcessor(bufferSize) {
				const script = {
					bufferSize,
					connections: [],
					disconnectCount: 0,
					onaudioprocess: null,
					connect(destination) {
						this.connections.push(destination);
					},
					disconnect() {
						this.disconnectCount++;
					},
				};
				scriptNodes.push(script);
				return script;
			},
			currentTime: 0,
			destination: { channelCount: 2 },
			onstatechange: null,
			outputLatency: 0,
			sampleRate: 48000,
			state: 'suspended',
			close() {
				this.closeCount++;
				return Promise.resolve();
			},
		};
		contexts.push(audioContext);
		return { audioContext, scriptNodes, sources };
	}

	function createSample(loopMode = 'disabled') {
		const audio = context.GodotAudio;
		audio.samples.set('stream', {
			getAudioBuffer: () => ({}),
			loopMode,
			sampleRate: 48000,
		});
		if (audio.buses.length === 0) {
			audio.buses.push({});
		}
		return audio.SampleNode.create({
			busIndex: 0,
			id: 'playback',
			streamObjectId: 'stream',
		}, { start: true });
	}

	return {
		closeAudio: () => new Promise((resolve, reject) => {
			context.GodotAudio.close_async(resolve, reject);
		}),
		context,
		createSample,
		finishedSamples,
		initAudio: () => context.GodotAudio.init(0, 0, () => {}, () => {}),
		makeContext,
		workletNodes,
	};
}

test('closing during worklet initialization prevents late node creation and start', async () => {
	const harness = createAudioHarness();
	const positionModule = deferred();
	const driverModule = deferred();
	const { audioContext } = harness.makeContext({
		'godot.audio.position.worklet.js': positionModule,
		'godot.audio.worklet.js': driverModule,
	});
	harness.initAudio();
	harness.context.GodotAudioWorklet.create(2);
	harness.context.GodotAudioWorklet.start(new Float32Array(), new Float32Array(), new Int32Array());

	await harness.closeAudio();
	driverModule.resolve();
	positionModule.resolve();
	await flushPromises();

	assert.equal(audioContext.closeCount, 1);
	assert.equal(harness.workletNodes.length, 0);
});

test('a stale worklet completion cannot affect a reinitialized audio context', async () => {
	const harness = createAudioHarness();
	const oldDriverModule = deferred();
	const oldPositionModule = deferred();
	const oldContext = harness.makeContext({
		'godot.audio.position.worklet.js': oldPositionModule,
		'godot.audio.worklet.js': oldDriverModule,
	});
	harness.initAudio();
	harness.context.GodotAudioWorklet.create(2);
	harness.context.GodotAudioWorklet.start(new Float32Array(), new Float32Array(), new Int32Array());
	await harness.closeAudio();

	const newDriverModule = deferred();
	const newPositionModule = deferred();
	const newContext = harness.makeContext({
		'godot.audio.position.worklet.js': newPositionModule,
		'godot.audio.worklet.js': newDriverModule,
	});
	harness.initAudio();
	harness.context.GodotAudioWorklet.create(6);
	harness.context.GodotAudioWorklet.start(new Float32Array(), new Float32Array(), new Int32Array());

	oldDriverModule.resolve();
	oldPositionModule.resolve();
	await flushPromises();
	assert.equal(harness.workletNodes.length, 0);

	newDriverModule.resolve();
	newPositionModule.resolve();
	await flushPromises();
	assert.equal(harness.workletNodes.length, 1);
	assert.equal(harness.workletNodes[0].context, newContext.audioContext);
	assert.equal(harness.workletNodes[0].connections.length, 1);
	assert.equal(harness.workletNodes[0].connections[0], newContext.audioContext.destination);
	assert.equal(harness.workletNodes[0].messages[0].cmd, 'start');
	assert.equal(oldContext.audioContext.closeCount, 1);
	await harness.closeAudio();
});

test('a sample waiting for the position worklet cannot start after close', async () => {
	const harness = createAudioHarness();
	const positionModule = deferred();
	harness.makeContext({
		'godot.audio.position.worklet.js': positionModule,
	});
	harness.initAudio();
	const fakeSample = {
		_context: harness.context.GodotAudio.ctx,
		_contextGeneration: harness.context.GodotAudio.contextGeneration,
		_source: { connect() {} },
		getPositionWorklet() {
			throw new Error('Position worklet must not be created after close.');
		},
		isCanceled: false,
		start() {
			throw new Error('Sample must not start after close.');
		},
	};
	const connecting = harness.context.SampleNode.prototype.connectPositionWorklet.call(fakeSample, true);
	await harness.closeAudio();
	positionModule.resolve();
	await connecting;
});

test('a stale media input completion is stopped without creating an audio node', async () => {
	const harness = createAudioHarness();
	const mediaRequest = deferred();
	const { audioContext } = harness.makeContext();
	let callbackCalled = false;
	let trackStopped = false;
	harness.context.navigator.mediaDevices = {
		getUserMedia: () => mediaRequest.promise,
	};
	harness.initAudio();
	harness.context.GodotAudio.create_input(() => {
		callbackCalled = true;
	});
	await harness.closeAudio();
	mediaRequest.resolve({
		getTracks: () => [{ stop: () => {
			trackStopped = true;
		} }],
	});
	await flushPromises();

	assert.equal(audioContext.mediaSourceCount, 0);
	assert.equal(callbackCalled, false);
	assert.equal(trackStopped, true);
});

test('normal script processor initialization starts and closes the current node', async () => {
	const harness = createAudioHarness();
	const { audioContext, scriptNodes } = harness.makeContext();
	harness.initAudio();
	assert.equal(harness.context.GodotAudioScript.create(512, 2), 512);
	harness.context.GodotAudioScript.start(0, 0, 0, 0, () => {});

	assert.deepEqual(scriptNodes[0].connections, [audioContext.destination]);
	assert.equal(typeof scriptNodes[0].onaudioprocess, 'function');
	await harness.closeAudio();
	assert.equal(scriptNodes[0].disconnectCount, 1);
	assert.equal(scriptNodes[0].onaudioprocess, null);
});

test('queued sample position messages cannot read a replacement context registry', async () => {
	const harness = createAudioHarness();
	harness.makeContext();
	harness.initAudio();
	const sample = harness.createSample();
	await flushPromises();
	const queuedPosition = sample._positionWorklet.port.onmessage;
	queuedPosition({ data: { type: 'position', data: '48000' } });
	assert.equal(sample._playbackPosition, 1);
	await harness.closeAudio();
	harness.makeContext();
	harness.initAudio();
	for (const canceled of [true, false]) {
		sample.isCanceled = canceled;
		assert.doesNotThrow(() => queuedPosition({ data: { type: 'position', data: '96000' } }));
		assert.equal(sample._playbackPosition, 1);
	}
	await harness.closeAudio();
});

test('queued output worklet messages cannot process a replacement context buffer', async () => {
	const harness = createAudioHarness();
	const driver = harness.context.GodotAudioWorklet;
	harness.makeContext();
	harness.initAudio();
	driver.create(2);
	driver.start_no_threads(0, 8, () => {}, 0, 8, () => {});
	await flushPromises();
	const queuedMessage = driver.worklet.port.onmessage;
	await harness.closeAudio();
	harness.makeContext();
	harness.initAudio();
	driver.create(2);
	driver.start_no_threads(0, 8, () => {}, 0, 8, () => {});
	await flushPromises();
	let consumed = 0;
	let received = 0;
	driver.ring_buffer = {
		consumed() {
			consumed++;
		},
		receive() {
			received++;
		},
	};
	const read = { data: { cmd: 'read', data: 4 } };
	const input = { data: { cmd: 'input', data: new Float32Array(4) } };
	queuedMessage(read);
	queuedMessage(input);
	assert.equal(consumed, 0);
	assert.equal(received, 0);
	driver.worklet.port.onmessage(read);
	driver.worklet.port.onmessage(input);
	assert.equal(consumed, 1);
	assert.equal(received, 1);
	await harness.closeAudio();
});

for (const loopMode of ['disabled', 'forward', 'backward']) {
	test(`old ended callbacks cannot affect same-ID playback after reinit (${loopMode})`, async () => {
		const harness = createAudioHarness();
		harness.makeContext();
		harness.initAudio();
		const oldSample = harness.createSample();
		await flushPromises();
		const oldSource = oldSample._source;
		const queuedEnded = oldSource.listeners.get('ended');
		const closing = harness.closeAudio();
		assert.equal(oldSample.isCanceled, true);

		const currentContext = harness.makeContext();
		harness.initAudio();
		const replacement = harness.createSample(loopMode);
		await flushPromises();
		await closing;
		const currentSource = replacement._source;
		queuedEnded();

		assert.equal(harness.context.GodotAudio.sampleNodes.get('playback'), replacement);
		assert.equal(oldSample._source, oldSource);
		assert.equal(oldSource.stopCount, 0);
		assert.equal(replacement._source, currentSource);
		assert.equal(currentSource.startCount, 1);
		assert.equal(currentSource.stopCount, 0);
		assert.equal(currentContext.sources.length, 1);
		assert.equal(harness.context.GodotAudio.audioPositionWorkletNodes.length, 0);
		assert.deepEqual(harness.finishedSamples, []);
		await harness.closeAudio();
	});
}

for (const invalidation of ['cancellation', 'context', 'generation']) {
	test(`ended callbacks check ${invalidation} before reading the sample registry`, async () => {
		const harness = createAudioHarness();
		const { audioContext } = harness.makeContext();
		harness.initAudio();
		const sample = harness.createSample();
		await flushPromises();
		const audio = harness.context.GodotAudio;
		const queuedEnded = sample._onended;
		switch (invalidation) {
		case 'cancellation':
			sample.isCanceled = true;
			break;
		case 'context':
			audio.ctx = harness.makeContext().audioContext;
			break;
		case 'generation':
			audio.contextGeneration++;
			break;
		default:
			assert.fail('Unexpected invalidation.');
		}
		audio.samples.clear();
		assert.doesNotThrow(() => queuedEnded());
		assert.deepEqual(harness.finishedSamples, []);
		sample.clear();
		assert.equal(audio.audioPositionWorkletNodes.length, 0);
		audio.ctx = audioContext;
		await harness.closeAudio();
	});
}

for (const registered of [true, false]) {
	test(`stale sample cleanup cannot pool old worklets or delete new playback (registered: ${registered})`, async () => {
		const harness = createAudioHarness();
		harness.makeContext();
		harness.initAudio();
		const oldSample = harness.createSample();
		await flushPromises();
		const oldSource = oldSample._source;
		const oldWorklet = oldSample._positionWorklet;
		const oldBus = oldSample._sampleNodeBuses.values().next().value;
		const audio = harness.context.GodotAudio;
		if (!registered) {
			audio.sampleNodes.delete(oldSample.id);
		}
		await harness.closeAudio();
		assert.equal(oldSample.isCanceled, registered);
		harness.makeContext();
		harness.initAudio();
		const replacement = harness.createSample();
		await flushPromises();
		const pool = audio.audioPositionWorkletNodes;
		oldSample.clear();

		assert.equal(audio.audioPositionWorkletNodes, pool);
		assert.equal(pool.length, 0);
		assert.equal(audio.sampleNodes.get('playback'), replacement);
		assert.deepEqual(harness.finishedSamples, []);
		assert.equal(oldSource.stopCount, 1);
		assert.equal(oldSource.disconnectCount, 1);
		assert.equal(oldSource.listeners.has('ended'), false);
		assert.equal(oldWorklet.disconnectCount, 1);
		assert.equal(oldWorklet.port.onmessage, null);
		assert.equal(oldBus.clearCount, 1);
		assert.equal(oldSample._source, null);
		assert.equal(oldSample._positionWorklet, null);
		oldSample.clear();
		assert.equal(pool.length, 0);
		assert.equal(audio.sampleNodes.get('playback'), replacement);
		assert.deepEqual(harness.finishedSamples, []);
		await harness.closeAudio();
	});
}

test('normal sample completion disposes once and recycles its worklet for same-ID playback', async () => {
	const harness = createAudioHarness();
	harness.makeContext();
	harness.initAudio();
	const sample = harness.createSample();
	await flushPromises();
	const audio = harness.context.GodotAudio;
	const source = sample._source;
	const worklet = sample._positionWorklet;
	const bus = sample._sampleNodeBuses.values().next().value;
	const queuedEnded = source.listeners.get('ended');
	queuedEnded();

	assert.equal(sample.isCanceled, true);
	assert.equal(source.stopCount, 1);
	assert.equal(source.disconnectCount, 1);
	assert.equal(source.listeners.has('ended'), false);
	assert.equal(bus.clearCount, 1);
	assert.equal(worklet.disconnectCount, 1);
	assert.equal(worklet.port.onmessage, null);
	assert.equal(audio.sampleNodes.has('playback'), false);
	assert.deepEqual(harness.finishedSamples, ['playback']);
	assert.equal(audio.audioPositionWorkletNodes.length, 1);
	assert.equal(audio.audioPositionWorkletNodes[0], worklet);

	const replacement = harness.createSample('forward');
	await flushPromises();
	assert.equal(replacement._positionWorklet, worklet);
	assert.equal(audio.audioPositionWorkletNodes.length, 0);
	queuedEnded();
	sample.clear();
	assert.equal(sample._source, null);
	assert.equal(audio.sampleNodes.get('playback'), replacement);
	assert.equal(audio.audioPositionWorkletNodes.length, 0);
	assert.deepEqual(harness.finishedSamples, ['playback']);
	await harness.closeAudio();
});

for (const loopMode of ['forward', 'backward']) {
	test(`normal ${loopMode} samples still restart, pause and dispose`, async () => {
		const harness = createAudioHarness();
		const { sources } = harness.makeContext();
		harness.initAudio();
		const sample = harness.createSample(loopMode);
		await flushPromises();
		const source = sample._source;
		source.listeners.get('ended')();
		const restartedSource = sample._source;

		assert.notEqual(restartedSource, source);
		assert.equal(sources.length, 2);
		assert.equal(source.disconnectCount, 1);
		assert.equal(restartedSource.startCount, 1);
		assert.equal(harness.context.GodotAudio.sampleNodes.get('playback'), sample);
		assert.deepEqual(harness.finishedSamples, []);
		sample.pause();
		restartedSource.listeners.get('ended')();
		assert.equal(sample._source, restartedSource);
		assert.equal(sources.length, 2);
		assert.deepEqual(harness.finishedSamples, []);
		sample.stop();
		assert.equal(harness.context.GodotAudio.sampleNodes.has('playback'), false);
		assert.deepEqual(harness.finishedSamples, ['playback']);
		assert.equal(harness.context.GodotAudio.audioPositionWorkletNodes.length, 1);
		await harness.closeAudio();
	});
}
